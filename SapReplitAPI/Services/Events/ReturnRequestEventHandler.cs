using Microsoft.Data.SqlClient;
using SapReplitAPI.Models.Cache;
using SapReplitAPI.Models.InvoiceLifecycle;
using SapReplitAPI.Services.CachedServices;
using SapReplitAPI.Services.Neon;

namespace SapReplitAPI.Services.Events;

/// <summary>
/// Handles ObjectType=234000031 (ORRR / Return Request), TransactionType=A/U/C.
///
/// Event flow:
///   A (Add):    New return request created → sync to Neon
///   U (Update): Return request modified (qty, status, cancellation) → sync to Neon
///   C (Cancel): Return request cancelled (CANCELED=Y) → sync to Neon
///
/// For each ORRR event, this handler:
/// 1. Reads ORRR header + RRR1 lines from SAP
/// 2. Persists to Neon ReturnRequests + ReturnRequestLines (new tables)
/// 3. Triggers invoice refresh to recompute PendingReturnQty and ReturnableQty
/// 4. Updates InvoiceLines.PendingReturnQty (new computed column) in Neon
///
/// Note: Inventory refresh is skipped for return requests (RRR1 items are not yet moved).
/// </summary>
public sealed class ReturnRequestEventHandler : ISapEventHandler
{
    // SAP object type for ORRR (Return Request)
    private const int OrrrObjectType = 234000031;

    private readonly SapService _sap;
    private readonly NeonReturnRequestWriteService _neonReturnRequest;
    private readonly InvoiceCacheService _invoiceCache;
    private readonly NeonEventWriteService _neonInvoice;
    private readonly ILogger<ReturnRequestEventHandler> _logger;

    public ReturnRequestEventHandler(
        SapService sap,
        NeonReturnRequestWriteService neonReturnRequest,
        InvoiceCacheService invoiceCache,
        NeonEventWriteService neonInvoice,
        ILogger<ReturnRequestEventHandler> logger)
    {
        _sap = sap;
        _neonReturnRequest = neonReturnRequest;
        _invoiceCache = invoiceCache;
        _neonInvoice = neonInvoice;
        _logger = logger;
    }

    public bool CanHandle(SapOutboxEvent ev)
        => ev.ObjectType == "234000031" &&
           (ev.TransactionType == "A" || ev.TransactionType == "U" || ev.TransactionType == "C");

    public async Task<(bool ok, string? error)> HandleAsync(SapOutboxEvent ev, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            // 1) Validate DocEntry
            if (ev.DocEntry is not { } orrrDocEntry)
            {
                _logger.LogWarning("[ReturnRequestHandler] EventId={EventId} has null DocEntry.", ev.EventId);
                return (false, "DocEntry is null for ORRR event");
            }

            // 2) Read ORRR header + RRR1 lines from SAP
            var returnRequest = await _sap.GetReturnRequestByDocEntryAsync(orrrDocEntry, ct);
            if (returnRequest is null)
            {
                _logger.LogWarning(
                    "[ReturnRequestHandler] DocEntry={DocEntry} not found in ORRR — EventId={EventId}.",
                    orrrDocEntry, ev.EventId);
                return (false, $"ORRR DocEntry={orrrDocEntry} not found in SAP");
            }

            // 3) Sync ORRR + RRR1 to Neon (new tables)
            await _neonReturnRequest.UpsertReturnRequestAsync(returnRequest, ct);

            // 4) Resolve affected invoices (BaseType=13 on RRR1 lines)
            var affectedInvoiceDocEntries = returnRequest.Lines
                .Where(l => l.BaseType == 13 && l.BaseEntry > 0)
                .Select(l => l.BaseEntry)
                .Distinct()
                .ToList();

            if (affectedInvoiceDocEntries.Count == 0)
            {
                _logger.LogWarning(
                    "[ReturnRequestHandler] ReturnRequestNoInvoiceLink: DocEntry={DocEntry} DocNum={DocNum} " +
                    "EventId={EventId}. RRR1 has no BaseType=13 rows.",
                    orrrDocEntry, returnRequest.DocNum, ev.EventId);
                sw.Stop();
                return (true, null);  // Not a failure — return request may be for delivery lines (BaseType=15)
            }

            // 5) Refresh affected invoices — recalculates PendingReturnQty in Neon
            int refreshed = 0;
            foreach (var invDocEntry in affectedInvoiceDocEntries)
            {
                await RefreshInvoiceAsync(invDocEntry, orrrDocEntry, ev.EventId, ct);
                refreshed++;
            }

            sw.Stop();
            _logger.LogInformation(
                "[ReturnRequestHandler] Done: OrrrDocEntry={DocEntry} DocNum={DocNum} " +
                "AffectedInvoices={AffectedCount} Refreshed={Refreshed} RrrLines={RrrLines} {Elapsed:F1}ms EventId={EventId}",
                orrrDocEntry, returnRequest.DocNum, affectedInvoiceDocEntries.Count,
                refreshed, returnRequest.Lines.Count, sw.Elapsed.TotalMilliseconds, ev.EventId);

            return (true, null);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex,
                "[ReturnRequestHandler] Failed for DocEntry={DocEntry} EventId={EventId} after {Elapsed:F1}ms",
                ev.DocEntry, ev.EventId, sw.Elapsed.TotalMilliseconds);
            return (false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // Refreshes a single OINV row in SQLite and Neon. Throws on failure so the caller
    // (HandleAsync) can return (false, error) and the event will retry.
    private async Task RefreshInvoiceAsync(
        int invDocEntry,
        int orrrDocEntry,
        Guid eventId,
        CancellationToken ct)
    {
        var dto = await _sap.GetInvoiceByDocEntryAsync(invDocEntry, ct);
        if (dto is null)
        {
            _logger.LogWarning(
                "[ReturnRequestHandler] Affected invoice DocEntry={InvDocEntry} not found in SAP " +
                "(linked from ORRR DocEntry={OrrrDocEntry}) EventId={EventId} — skipping.",
                invDocEntry, orrrDocEntry, eventId);
            return;
        }

        var lifecycle = _sap.GetInvoiceLifecycleStatusResults(new[] { invDocEntry });
        var header = MapToHeader(dto, lifecycle);
        var lines = MapToLines(dto);

        await _invoiceCache.UpsertSingleInvoiceAsync(header, lines, ct);
        await _neonInvoice.UpsertInvoiceAsync(header, lines, ct);

        _logger.LogDebug(
            "[ReturnRequestHandler] Refreshed invoice DocEntry={InvDocEntry} Status={Status} DocStatusDisplay={Display}",
            invDocEntry, header.DocStatus, header.DocStatusDisplay);
    }

    private static CachedInvoice MapToHeader(
        InvoiceDto dto,
        IReadOnlyDictionary<int, InvoiceLifecycleStatusResult> lifecycle)
    {
        string docStatus = dto.Status ?? "";
        string canceled = dto.Canceled ?? "";

        string docStatusDisplay = lifecycle.TryGetValue(dto.DocEntry, out var lc)
            ? lc.DocStatusDisplay
            : ComputeLegacyStatusDisplay(docStatus, canceled);

        return new CachedInvoice
        {
            DocEntry = dto.DocEntry,
            DocNum = dto.DocNum,
            InvoiceDocNum = dto.DocNum,
            DocDate = dto.DocDate,
            DocStatus = docStatus,
            Canceled = canceled,
            DocStatusDisplay = docStatusDisplay,
            CardCode = dto.CardCode ?? "",
            CardName = dto.CardName ?? "",
            DocTotal = dto.DocTotal,
            PaidToDate = dto.PaidToDate,
            BalanceDue = dto.BalanceDue,
            DaysOverdue = dto.DaysOverdue,
            SalesEmployeeCode = dto.SalesEmployeeCode,
            SalesEmployeeName = dto.SalesEmployeeName ?? "",
            GroupNum = dto.GroupNum
        };
    }

    private static List<CachedInvoiceLine> MapToLines(InvoiceDto dto)
    {
        var lines = new List<CachedInvoiceLine>(dto.Lines?.Count ?? 0);
        foreach (var l in dto.Lines ?? Enumerable.Empty<InvoiceLineDto>())
        {
            lines.Add(new CachedInvoiceLine
            {
                DocEntry = dto.DocEntry,
                LineNum = (int)l.LineNum,
                ItemCode = l.ItemCode ?? "",
                Dscription = l.Dscription ?? "",
                Quantity = l.Quantity,
                Price = l.Price,
                LineTotal = l.LineTotal,
                U_Item_Name = l.U_Item_Name ?? "",
                U_MdlTEST = l.U_MdlTEST ?? ""
            });
        }
        return lines;
    }

    private static string ComputeLegacyStatusDisplay(string docStatus, string canceled)
    {
        if (docStatus == "O") return "Open";
        if (docStatus == "C")
        {
            if (string.Equals(canceled, "Canceled", StringComparison.OrdinalIgnoreCase)) return "Cancelled";
            if (string.Equals(canceled, "Cancellation", StringComparison.OrdinalIgnoreCase)) return "Cancellation";
            return "Closed";
        }
        return string.IsNullOrWhiteSpace(docStatus) ? "Unknown" : docStatus;
    }
}
