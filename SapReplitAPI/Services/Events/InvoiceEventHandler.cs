using SapReplitAPI.Services.CachedServices;
using SapReplitAPI.Services.Inventory;
using SapReplitAPI.Services.Neon;
using SapReplitAPI.Services.ZoneFulfillment;

namespace SapReplitAPI.Services.Events;

/// <summary>
/// Handles ObjectType=13 (OINV), TransactionType=A.
/// Core mirror write delegated to InvoiceMirrorRefreshService.
/// Phase 2: inventory refresh (INV1 item codes) + delivery cache refresh (INV1.BaseType=15 refs).
/// Never advances SyncMetadata["Invoice"] / NeonMirror:Invoices / NeonMirror:Deliveries / Delivery watermarks.
/// </summary>
public sealed class InvoiceEventHandler : ISapEventHandler
{
    private readonly SapService _sap;
    private readonly InvoiceMirrorRefreshService _refresh;
    private readonly InventoryEventRefreshService _inv;
    private readonly DeliveryCacheService _deliveryCache;
    private readonly NeonDeliveryWriteService _neonDelivery;
    private readonly ZoneFulfillmentReportService _zfReport;
    private readonly ILogger<InvoiceEventHandler> _logger;

    public InvoiceEventHandler(
        SapService sap,
        InvoiceMirrorRefreshService refresh,
        InventoryEventRefreshService inv,
        DeliveryCacheService deliveryCache,
        NeonDeliveryWriteService neonDelivery,
        ZoneFulfillmentReportService zfReport,
        ILogger<InvoiceEventHandler> logger)
    {
        _sap          = sap;
        _refresh      = refresh;
        _inv          = inv;
        _deliveryCache = deliveryCache;
        _neonDelivery = neonDelivery;
        _zfReport     = zfReport;
        _logger       = logger;
    }

    public bool CanHandle(SapOutboxEvent ev)
        => ev.ObjectType == "13" && ev.TransactionType == "A";

    public async Task<(bool ok, string? error)> HandleAsync(SapOutboxEvent ev, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            // 1) Validate DocEntry
            if (ev.DocEntry is not { } docEntry)
            {
                _logger.LogWarning("[InvoiceHandler] EventId={EventId} has null DocEntry — skipping.", ev.EventId);
                return (false, "DocEntry is null for 13/A event");
            }

            _logger.LogInformation(
                "[INVOICE-FASTPATH] START DocEntry={DocEntry} EventId={EventId}",
                docEntry, ev.EventId);

            // 2) Core mirror: SAP read → lifecycle → SQLite → Neon
            var result = await _refresh.RefreshAsync(docEntry, ct);
            if (!result.Ok)
            {
                _logger.LogWarning("[InvoiceHandler] DocEntry={DocEntry} refresh failed: {Error} — EventId={EventId}.",
                    docEntry, result.Error, ev.EventId);
                return (false, result.Error);
            }

            _logger.LogInformation(
                "[INVOICE-FASTPATH] SAP_READ_DONE DocEntry={DocEntry} DocNum={DocNum} Lines={LineCount} ElapsedMs={ElapsedMs:F1}",
                docEntry, result.DocNum, result.LineCount, result.SapReadMs);

            _logger.LogInformation(
                "[INVOICE-FASTPATH] SQLITE_DONE DocEntry={DocEntry} ElapsedMs={ElapsedMs:F1}",
                docEntry, result.SapReadMs + result.SqliteMs);

            _logger.LogInformation(
                "[INVOICE-FASTPATH] NEON_DONE DocEntry={DocEntry} ElapsedMs={ElapsedMs:F1}",
                docEntry, result.SapReadMs + result.SqliteMs + result.NeonMs);

            var dto    = result.Dto!;
            var header = result.Header!;

            // 3) Physical inventory observability probe
            bool hasInventoryMovement = await _sap.CheckOinmAsync(13, docEntry, ct);
            if (hasInventoryMovement)
            {
                _logger.LogInformation(
                    "[InvoiceHandler] InvoicePhysicalInventoryDetected: DocEntry={DocEntry} DocNum={DocNum} EventId={EventId}. " +
                    "Invoice directly drove a stock posting in OINM.",
                    docEntry, dto.DocNum, ev.EventId);
            }

            // 4) ZF report snapshot — fire-and-forget within try/catch, MUST NOT block 13/A
            if (header.ZoneRef == "ZoneFulfillment")
            {
                try
                {
                    await _zfReport.CaptureSnapshotAsync(docEntry, dto, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "[ZF-REPORT] SnapshotFailed: DocEntry={DocEntry} DocNum={DocNum} EventId={EventId}. " +
                        "13/A pipeline continues normally.",
                        docEntry, dto.DocNum, ev.EventId);
                }
            }

            // 5) Phase 2 — inventory refresh (INV1 item codes)
            var invItemCodes = _sap.GetItemCodesFromLines("INV1", docEntry);
            if (invItemCodes.Count > 0)
                await _inv.RefreshFullInventoryAsync(invItemCodes, ct);

            // 6) Phase 2 — delivery refresh (INV1.BaseType=15 refs) — SQLite then Neon
            var invDeliveryRefs = _sap.GetBaseDeliveryDocEntries("INV1", docEntry);
            foreach (var dde in invDeliveryRefs)
            {
                var del = await _sap.GetDeliveryByDocEntryAsync(dde, ct);
                if (del != null)
                {
                    await _deliveryCache.UpsertDeliveryAsync(del, ct);
                    await _neonDelivery.UpsertDeliveryAsync(del, ct);
                }
            }

            // 7) Cancellation-pair refresh: SAP fires only ONE 13/A event — for the cancellation
            //    document (CANCELED='C'). The original invoice's state change (to CANCELED='Y') is
            //    NOT emitted as a separate event. The link comes from INV1.BaseEntry (SAP's
            //    authoritative document chain — confirmed from live MOLAS_Live_2021 data).
            if (dto.Canceled == "Cancellation")
            {
                var originalDocEntry = await _sap.GetOriginalInvoiceDocEntryAsync(docEntry, ct);
                if (originalDocEntry.HasValue)
                {
                    _logger.LogInformation(
                        "[InvoiceHandler] InvoiceCancellationPairDetected: CancellationDocEntry={CancellationDocEntry} " +
                        "OriginalDocEntry={OriginalDocEntry} EventId={EventId}",
                        docEntry, originalDocEntry.Value, ev.EventId);

                    var origResult = await _refresh.RefreshAsync(originalDocEntry.Value, ct);
                    if (origResult.Ok)
                    {
                        _logger.LogInformation(
                            "[InvoiceHandler] InvoiceCancellationPairRefreshed: CancellationDocEntry={CancellationDocEntry} " +
                            "OriginalDocEntry={OriginalDocEntry} ElapsedMs={ElapsedMs:F1} EventId={EventId}",
                            docEntry, originalDocEntry.Value, sw.Elapsed.TotalMilliseconds, ev.EventId);
                    }
                    else
                    {
                        _logger.LogWarning(
                            "[InvoiceHandler] CancellationPair: OriginalDocEntry={OriginalDocEntry} refresh failed: {Error} — " +
                            "original not refreshed. EventId={EventId}",
                            originalDocEntry.Value, origResult.Error, ev.EventId);
                    }
                }
                else
                {
                    _logger.LogWarning(
                        "[InvoiceHandler] CancellationDoc DocEntry={DocEntry} has no INV1.BaseEntry (BaseType=13) — " +
                        "original not identified. EventId={EventId}",
                        docEntry, ev.EventId);
                }
            }

            sw.Stop();
            _logger.LogInformation(
                "[INVOICE-FASTPATH] DONE DocEntry={DocEntry} DocNum={DocNum} Status={Status} " +
                "DocStatusDisplay={Display} Lines={LineCount} TotalMs={TotalMs:F1} EventId={EventId}",
                docEntry, dto.DocNum, header.DocStatus, header.DocStatusDisplay,
                result.LineCount, sw.Elapsed.TotalMilliseconds, ev.EventId);

            return (true, null);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "[InvoiceHandler] Failed for DocEntry={DocEntry} EventId={EventId} after {Elapsed:F1}ms",
                ev.DocEntry, ev.EventId, sw.Elapsed.TotalMilliseconds);
            return (false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}
