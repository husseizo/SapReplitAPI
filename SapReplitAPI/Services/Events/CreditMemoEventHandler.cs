using SapReplitAPI.Models.Cache;
using SapReplitAPI.Models.InvoiceLifecycle;
using SapReplitAPI.Models.Payments;
using SapReplitAPI.Services.CachedServices;
using SapReplitAPI.Services.Inventory;
using SapReplitAPI.Services.Neon;

namespace SapReplitAPI.Services.Events;

/// <summary>
/// Handles ObjectType=14 (ORIN / A/R Credit Memo), TransactionType=A.
///
/// Path A (direct): RIN1.BaseType=13  → affected invoice = RIN1.BaseEntry
/// Path B (ORRR):   RIN1.BaseType=234000031 → ORRR DocEntry = RIN1.BaseEntry
///                  → query RRR1.BaseType=13 → affected invoice = RRR1.BaseEntry
///
/// Inventory and delivery refresh runs for EVERY valid 14/A event regardless of whether
/// underlying invoices can be resolved.  If no invoice is found the event still
/// completes as Done with a structured warning — it does NOT fail and retry.
/// </summary>
public sealed class CreditMemoEventHandler : ISapEventHandler
{
    // SAP B1 object type for ORRR (Return Request) — confirmed from live ORRR.ObjType column
    private const int OrrrObjectType = 234000031;

    private readonly SapService _sap;
    private readonly InvoiceCacheService _cache;
    private readonly NeonEventWriteService _neon;
    private readonly InventoryEventRefreshService _inv;
    private readonly DeliveryCacheService _deliveryCache;
    private readonly NeonDeliveryWriteService _neonDelivery;
    private readonly ILogger<CreditMemoEventHandler> _logger;

    public CreditMemoEventHandler(
        SapService sap,
        InvoiceCacheService cache,
        NeonEventWriteService neon,
        InventoryEventRefreshService inv,
        DeliveryCacheService deliveryCache,
        NeonDeliveryWriteService neonDelivery,
        ILogger<CreditMemoEventHandler> logger)
    {
        _sap           = sap;
        _cache         = cache;
        _neon          = neon;
        _inv           = inv;
        _deliveryCache = deliveryCache;
        _neonDelivery  = neonDelivery;
        _logger        = logger;
    }

    public bool CanHandle(SapOutboxEvent ev)
        => ev.ObjectType == "14" && ev.TransactionType == "A";

    public async Task<(bool ok, string? error)> HandleAsync(SapOutboxEvent ev, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            // 1) Validate DocEntry
            if (ev.DocEntry is not { } creditMemoDocEntry)
            {
                _logger.LogWarning("[CreditMemoHandler] EventId={EventId} has null DocEntry.", ev.EventId);
                return (false, "DocEntry is null for 14/A event");
            }

            // 2) Read credit memo from SAP
            var creditMemo = await _sap.GetCreditMemoByDocEntryAsync(creditMemoDocEntry, ct);
            if (creditMemo is null)
            {
                _logger.LogWarning(
                    "[CreditMemoHandler] DocEntry={DocEntry} not found in ORIN — EventId={EventId}.",
                    creditMemoDocEntry, ev.EventId);
                return (false, $"CreditMemo DocEntry={creditMemoDocEntry} not found in SAP");
            }

            // 3) Physical inventory observability probe
            bool hasInventoryMovement = await _sap.CheckOinmAsync(14, creditMemoDocEntry, ct);
            if (hasInventoryMovement)
            {
                _logger.LogInformation(
                    "[CreditMemoHandler] CreditMemoPhysicalInventoryDetected: DocEntry={DocEntry} DocNum={DocNum} EventId={EventId}. " +
                    "Credit memo directly drove a stock return in OINM.",
                    creditMemoDocEntry, creditMemo.DocNum, ev.EventId);
            }

            // 4) Resolve affected invoice DocEntries — both paths, de-duplicated
            var affectedInvoiceDocEntries = await ResolveBaseInvoicesAsync(creditMemo, creditMemoDocEntry, ev.EventId, ct);

            // 5) Phase 2 — inventory refresh (RIN1 item codes).
            //    Runs for EVERY valid 14/A regardless of invoice resolution outcome.
            var cmItemCodes = _sap.GetItemCodesFromLines("RIN1", creditMemoDocEntry);
            if (cmItemCodes.Count > 0)
                await _inv.RefreshFullInventoryAsync(cmItemCodes, ct);

            // 6) Phase 2 — delivery refresh (RIN1.BaseType=15 refs).
            //    Also runs unconditionally.
            var cmDeliveryRefs = _sap.GetBaseDeliveryDocEntries("RIN1", creditMemoDocEntry);
            foreach (var dde in cmDeliveryRefs)
            {
                var del = await _sap.GetDeliveryByDocEntryAsync(dde, ct);
                if (del != null)
                {
                    await _deliveryCache.UpsertDeliveryAsync(del, ct);
                    await _neonDelivery.UpsertDeliveryAsync(del, ct);
                }
            }

            // 7) If no invoice references found — log and finish (not a failure)
            if (affectedInvoiceDocEntries.Count == 0)
            {
                _logger.LogWarning(
                    "[CreditMemoHandler] CreditMemoNoInvoiceResolved: DocEntry={DocEntry} DocNum={DocNum} " +
                    "EventId={EventId}. Inventory and delivery refresh completed. Invoice refresh skipped.",
                    creditMemoDocEntry, creditMemo.DocNum, ev.EventId);
                sw.Stop();
                return (true, null);
            }

            // 8) Full refresh for each affected invoice — SQLite + Neon
            int refreshed = 0;
            foreach (var invDocEntry in affectedInvoiceDocEntries)
            {
                await RefreshInvoiceAsync(invDocEntry, creditMemoDocEntry, ev.EventId, ct);
                refreshed++;
            }

            sw.Stop();
            _logger.LogInformation(
                "[CreditMemoHandler] Done: CreditMemoDocEntry={DocEntry} DocNum={DocNum} " +
                "AffectedInvoices={AffectedCount} Refreshed={Refreshed} " +
                "InventoryItems={InvItems} DeliveryRefs={DelRefs} {Elapsed:F1}ms EventId={EventId}",
                creditMemoDocEntry, creditMemo.DocNum, affectedInvoiceDocEntries.Count,
                refreshed, cmItemCodes.Count, cmDeliveryRefs.Count, sw.Elapsed.TotalMilliseconds, ev.EventId);

            return (true, null);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex,
                "[CreditMemoHandler] Failed for DocEntry={DocEntry} EventId={EventId} after {Elapsed:F1}ms",
                ev.DocEntry, ev.EventId, sw.Elapsed.TotalMilliseconds);
            return (false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // Collects DISTINCT base invoice DocEntries from ALL RIN1 lines, supporting:
    //   Path A: RIN1.BaseType == 13       → direct OINV link
    //   Path B: RIN1.BaseType == 234000031 → through ORRR → RRR1.BaseType=13 → OINV
    private async Task<List<int>> ResolveBaseInvoicesAsync(
        SapCreditMemoResult creditMemo,
        int creditMemoDocEntry,
        Guid eventId,
        CancellationToken ct)
    {
        var result = new HashSet<int>();

        foreach (var (baseEntry, baseType) in creditMemo.Lines)
        {
            if (baseEntry <= 0) continue;

            if (baseType == 13)
            {
                // Path A — direct OINV link
                result.Add(baseEntry);
            }
            else if (baseType == OrrrObjectType)
            {
                // Path B — credit memo was created from an ORRR (Return Request)
                // Query RRR1 to find the underlying OINV DocEntries
                var invoiceDocEntries = _sap.GetRrr1BaseInvoiceDocEntries(baseEntry);
                foreach (var de in invoiceDocEntries)
                    result.Add(de);

                if (invoiceDocEntries.Count == 0)
                {
                    _logger.LogWarning(
                        "[CreditMemoHandler] CreditMemoOrrrNoInvoiceLink: " +
                        "CreditMemoDocEntry={CmDocEntry} OrrrDocEntry={OrrrDocEntry} EventId={EventId}. " +
                        "RRR1 has no BaseType=13 rows for this Return Request.",
                        creditMemoDocEntry, baseEntry, eventId);
                }
                else
                {
                    _logger.LogInformation(
                        "[CreditMemoHandler] PathB resolved: OrrrDocEntry={OrrrDocEntry} → Invoices=[{Invoices}] EventId={EventId}",
                        baseEntry, string.Join(",", invoiceDocEntries), eventId);
                }
            }
        }

        return result.ToList();
    }

    // Refreshes a single OINV row in SQLite and Neon. Throws on failure so the caller
    // (HandleAsync) can return (false, error) and the event will retry.
    private async Task RefreshInvoiceAsync(
        int invDocEntry,
        int creditMemoDocEntry,
        Guid eventId,
        CancellationToken ct)
    {
        var dto = await _sap.GetInvoiceByDocEntryAsync(invDocEntry, ct);
        if (dto is null)
        {
            _logger.LogWarning(
                "[CreditMemoHandler] Affected invoice DocEntry={InvDocEntry} not found in SAP " +
                "(linked from CreditMemo DocEntry={CmDocEntry}) EventId={EventId} — skipping.",
                invDocEntry, creditMemoDocEntry, eventId);
            return;
        }

        var lifecycle = _sap.GetInvoiceLifecycleStatusResults(new[] { invDocEntry });
        var header    = MapToHeader(dto, lifecycle);
        var lines     = MapToLines(dto);

        await _cache.UpsertSingleInvoiceAsync(header, lines, ct);
        await _neon.UpsertInvoiceAsync(header, lines, ct);

        _logger.LogDebug(
            "[CreditMemoHandler] Refreshed invoice DocEntry={InvDocEntry} Status={Status} DocStatusDisplay={Display}",
            invDocEntry, header.DocStatus, header.DocStatusDisplay);
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
