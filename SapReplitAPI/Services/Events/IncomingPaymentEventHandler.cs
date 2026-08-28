using SapReplitAPI.Models.Cache;
using SapReplitAPI.Models.InvoiceLifecycle;
using SapReplitAPI.Models.Payments;

namespace SapReplitAPI.Services.Events;

/// <summary>
/// Handles ObjectType=24 (ORCT), TransactionType=A (add) or C (cancel).
/// Reconciles SQLite + Neon payment and affected invoice rows.
/// Never advances SyncMetadata or NeonMirror watermarks.
/// </summary>
public sealed class IncomingPaymentEventHandler : ISapEventHandler
{
    private readonly SapService _sap;
    private readonly InvoiceCacheService _cache;
    private readonly NeonEventWriteService _neon;
    private readonly ILogger<IncomingPaymentEventHandler> _logger;

    public IncomingPaymentEventHandler(
        SapService sap,
        InvoiceCacheService cache,
        NeonEventWriteService neon,
        ILogger<IncomingPaymentEventHandler> logger)
    {
        _sap    = sap;
        _cache  = cache;
        _neon   = neon;
        _logger = logger;
    }

    public bool CanHandle(SapOutboxEvent ev)
        => ev.ObjectType == "24" && (ev.TransactionType == "A" || ev.TransactionType == "C");

    public async Task<(bool ok, string? error)> HandleAsync(SapOutboxEvent ev, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            // 1) Validate DocEntry
            if (ev.DocEntry is not { } paymentDocEntry)
            {
                _logger.LogWarning("[PaymentHandler] EventId={EventId} has null DocEntry.", ev.EventId);
                return (false, "DocEntry is null for 24/A|C event");
            }

            bool isCancellation = ev.TransactionType == "C";

            // 2) Snapshot existing SQLite payment invoice DocEntries BEFORE any changes
            var existingDocEntries = await _cache.GetExistingInvoiceDocEntriesForPaymentAsync(paymentDocEntry);

            // 3) Read current SAP state for this payment
            var sapPayments = await _sap.GetPaymentByDocEntryAsync(paymentDocEntry, ct);

            // 4) Zero-RCT2 guard: if no lines in SAP, log and mark Done
            if (sapPayments.Count == 0)
            {
                _logger.LogInformation(
                    "[PaymentHandler] Zero-RCT2: PaymentDocEntry={PaymentDocEntry} EventId={EventId} " +
                    "has no RCT2 lines in SAP. This can occur for on-account (no-invoice) receipts. Marking Done.",
                    paymentDocEntry, ev.EventId);
                return (true, null);
            }

            // 5) Map SAP rows to CachedInvoicePayment
            //    For 24/C (cancellation): override Canceled = true regardless of SAP field
            var now = DateTime.UtcNow;
            var currentPayments = sapPayments
                .Select(p => new CachedInvoicePayment
                {
                    DocEntry              = p.DocEntry,
                    PaymentDocEntry       = p.PaymentDocEntry,
                    PaymentNumber         = p.PaymentNumber,
                    InvoiceDocNum         = p.InvoiceDocNum,
                    PaymentDate           = p.PaymentDate,
                    CardCode              = p.CardCode ?? "",
                    CardName              = p.CardName ?? "",
                    AmountApplied         = p.AmountApplied,
                    BankTransferAmount    = p.BankTransferAmount,
                    BankTransferReference = p.BankTransferReference ?? "",
                    DebitAccountCode      = p.DebitAccountCode ?? "",
                    DebitAccountName      = p.DebitAccountName ?? "",
                    SalesEmployeeCode     = p.SalesEmployeeCode ?? "",
                    SalesEmployeeName     = p.SalesEmployeeName ?? "",
                    ClientReference       = p.ClientReference ?? "",
                    Canceled              = isCancellation || p.Canceled,
                    CounterRef            = p.CounterRef ?? "",
                    LastUpdated           = now
                })
                .ToList();

            // 6) SQLite: UPSERT current payments + DELETE stale (one tx)
            await _cache.ReconcilePaymentsForPaymentDocEntryAsync(paymentDocEntry, currentPayments, ct);

            // Compute full set of affected invoice DocEntries (snapshot ∪ current SAP)
            var currentDocEntries   = currentPayments.Select(p => p.DocEntry).Distinct().ToList();
            var affectedDocEntries  = existingDocEntries.Union(currentDocEntries).Distinct().ToList();

            // 7) Neon: UPSERT current payments + DELETE stale (one tx)
            await _neon.UpsertPaymentsAsync(currentPayments, paymentDocEntry, currentDocEntries, ct);

            // 8) Refresh each affected invoice in SQLite + Neon
            int refreshed = 0;
            foreach (var invDocEntry in affectedDocEntries)
            {
                try
                {
                    await RefreshInvoiceAsync(invDocEntry, ct);
                    refreshed++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "[PaymentHandler] Failed to refresh affected invoice DocEntry={InvDocEntry} " +
                        "for PaymentDocEntry={PaymentDocEntry} EventId={EventId}",
                        invDocEntry, paymentDocEntry, ev.EventId);
                    // Continue refreshing other invoices; the payment itself is already reconciled.
                    // The failed invoice will be corrected on the next DeltaSyncJob run.
                }
            }

            sw.Stop();
            _logger.LogInformation(
                "[PaymentHandler] Done: PaymentDocEntry={PaymentDocEntry} TransType={TransType} " +
                "Payments={PaymentCount} AffectedInvoices={AffectedCount} Refreshed={Refreshed} " +
                "{Elapsed:F1}ms EventId={EventId}",
                paymentDocEntry, ev.TransactionType, currentPayments.Count,
                affectedDocEntries.Count, refreshed, sw.Elapsed.TotalMilliseconds, ev.EventId);

            return (true, null);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex,
                "[PaymentHandler] Failed for DocEntry={DocEntry} EventId={EventId} after {Elapsed:F1}ms",
                ev.DocEntry, ev.EventId, sw.Elapsed.TotalMilliseconds);
            return (false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task RefreshInvoiceAsync(int docEntry, CancellationToken ct)
    {
        var dto = await _sap.GetInvoiceByDocEntryAsync(docEntry, ct);
        if (dto is null)
        {
            _logger.LogWarning("[PaymentHandler] Affected invoice DocEntry={DocEntry} not found in SAP — skipping refresh.", docEntry);
            return;
        }

        var lifecycle = _sap.GetInvoiceLifecycleStatusResults(new[] { docEntry });
        var header    = MapToHeader(dto, lifecycle);
        var lines     = MapToLines(dto);

        await _cache.UpsertSingleInvoiceAsync(header, lines, ct);
        await _neon.UpsertInvoiceAsync(header, lines, ct);
    }

    private static CachedInvoice MapToHeader(
        InvoiceDto dto,
        IReadOnlyDictionary<int, InvoiceLifecycleStatusResult> lifecycle)
    {
        string docStatus = dto.Status ?? "";
        string canceled  = dto.Canceled ?? "";

        string docStatusDisplay = lifecycle.TryGetValue(dto.DocEntry, out var lc)
            ? lc.DocStatusDisplay
            : ComputeLegacyStatusDisplay(docStatus, canceled);

        return new CachedInvoice
        {
            DocEntry          = dto.DocEntry,
            DocNum            = dto.DocNum,
            InvoiceDocNum     = dto.DocNum,
            DocDate           = dto.DocDate,
            DocStatus         = docStatus,
            Canceled          = canceled,
            DocStatusDisplay  = docStatusDisplay,
            CardCode          = dto.CardCode ?? "",
            CardName          = dto.CardName ?? "",
            DocTotal          = dto.DocTotal,
            PaidToDate        = dto.PaidToDate,
            BalanceDue        = dto.BalanceDue,
            DaysOverdue       = dto.DaysOverdue,
            SalesEmployeeCode = dto.SalesEmployeeCode,
            SalesEmployeeName = dto.SalesEmployeeName ?? "",
            GroupNum          = dto.GroupNum
        };
    }

    private static List<CachedInvoiceLine> MapToLines(InvoiceDto dto)
    {
        var lines = new List<CachedInvoiceLine>(dto.Lines?.Count ?? 0);
        foreach (var l in dto.Lines ?? Enumerable.Empty<InvoiceLineDto>())
        {
            lines.Add(new CachedInvoiceLine
            {
                DocEntry    = dto.DocEntry,
                LineNum     = (int)l.LineNum,
                ItemCode    = l.ItemCode ?? "",
                Dscription  = l.Dscription ?? "",
                Quantity    = l.Quantity,
                Price       = l.Price,
                LineTotal   = l.LineTotal,
                U_Item_Name = l.U_Item_Name ?? "",
                U_MdlTEST   = l.U_MdlTEST ?? ""
            });
        }
        return lines;
    }

    private static string ComputeLegacyStatusDisplay(string docStatus, string canceled)
    {
        if (docStatus == "O") return "Open";
        if (docStatus == "C")
        {
            if (string.Equals(canceled, "Canceled",     StringComparison.OrdinalIgnoreCase)) return "Cancelled";
            if (string.Equals(canceled, "Cancellation", StringComparison.OrdinalIgnoreCase)) return "Cancellation";
            return "Closed";
        }
        return string.IsNullOrWhiteSpace(docStatus) ? "Unknown" : docStatus;
    }
}
