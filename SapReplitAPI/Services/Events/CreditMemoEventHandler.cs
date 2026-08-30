using SapReplitAPI.Models.Cache;
using SapReplitAPI.Models.InvoiceLifecycle;
using SapReplitAPI.Models.Payments;
using SapReplitAPI.Services.CachedServices;
using SapReplitAPI.Services.Inventory;
using SapReplitAPI.Services.Neon;

namespace SapReplitAPI.Services.Events;

/// <summary>
/// Handles ObjectType=14 (ORIN / A/R Credit Memo), TransactionType=A.
/// Phase 1: refreshes linked base invoices (RIN1.BaseEntry where BaseType=13) in SQLite + Neon.
/// Phase 2: inventory refresh (RIN1 item codes) + delivery cache refresh (RIN1.BaseType=15 refs).
/// Never advances SyncMetadata or NeonMirror watermarks (including Delivery / NeonMirror:Deliveries).
/// </summary>
public sealed class CreditMemoEventHandler : ISapEventHandler
{
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

            // 4) Collect DISTINCT base invoice DocEntries from ALL RIN1 lines where BaseType=13
            var affectedInvoiceDocEntries = creditMemo.Lines
                .Where(l => l.BaseType == 13 && l.BaseEntry > 0)
                .Select(l => l.BaseEntry)
                .Distinct()
                .ToList();

            if (affectedInvoiceDocEntries.Count == 0)
            {
                _logger.LogInformation(
                    "[CreditMemoHandler] CreditMemo DocEntry={DocEntry} has no RIN1 lines linking to OINV (BaseType=13). " +
                    "No invoice refresh needed. Marking Done. EventId={EventId}",
                    creditMemoDocEntry, ev.EventId);
                return (true, null);
            }

            // 5) Full refresh for each affected invoice — SQLite + Neon
            //    Whole event fails if any invoice fails (handler returns false; retry scheduled).
            int refreshed = 0;
            foreach (var invDocEntry in affectedInvoiceDocEntries)
            {
                await RefreshInvoiceAsync(invDocEntry, creditMemoDocEntry, ev.EventId, ct);
                refreshed++;
            }

            // 6) Phase 2 — inventory refresh (RIN1 item codes, physical return movement)
            var cmItemCodes = _sap.GetItemCodesFromLines("RIN1", creditMemoDocEntry);
            if (cmItemCodes.Count > 0)
                await _inv.RefreshFullInventoryAsync(cmItemCodes, ct);

            // 7) Phase 2 — delivery refresh (RIN1.BaseType=15 refs) — SQLite then Neon
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
