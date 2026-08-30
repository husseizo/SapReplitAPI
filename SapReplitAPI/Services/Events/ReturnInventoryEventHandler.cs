using SapReplitAPI.Services.CachedServices;
using SapReplitAPI.Services.Inventory;
using SapReplitAPI.Services.Neon;

namespace SapReplitAPI.Services.Events;

/// <summary>
/// Handles ObjectType=16 (ORDN), TransactionType=A.
/// Refreshes inventory for returned items + refreshes referenced deliveries (RDN1.BaseType=15).
/// Evidence label: EXPECTED.
/// Never advances SyncMetadata watermarks.
/// </summary>
public sealed class ReturnInventoryEventHandler : ISapEventHandler
{
    private readonly SapService _sap;
    private readonly DeliveryCacheService _deliveryCache;
    private readonly NeonDeliveryWriteService _neonDelivery;
    private readonly InventoryEventRefreshService _inv;
    private readonly ILogger<ReturnInventoryEventHandler> _log;

    public ReturnInventoryEventHandler(
        SapService sap,
        DeliveryCacheService deliveryCache,
        NeonDeliveryWriteService neonDelivery,
        InventoryEventRefreshService inv,
        ILogger<ReturnInventoryEventHandler> log)
    {
        _sap           = sap;
        _deliveryCache = deliveryCache;
        _neonDelivery  = neonDelivery;
        _inv           = inv;
        _log           = log;
    }

    public bool CanHandle(SapOutboxEvent ev)
        => ev.ObjectType == "16" && ev.TransactionType == "A";

    public async Task<(bool ok, string? error)> HandleAsync(SapOutboxEvent ev, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            if (ev.DocEntry is not { } docEntry)
            {
                _log.LogWarning("[ReturnHandler] EventId={EventId} null DocEntry.", ev.EventId);
                return (false, "DocEntry is null for 16/A event");
            }

            // 1. Inventory refresh for returned item codes (RDN1)
            var itemCodes = _sap.GetItemCodesFromLines("RDN1", docEntry);
            if (itemCodes.Count > 0)
                await _inv.RefreshFullInventoryAsync(itemCodes, ct);

            // 2. Refresh referenced deliveries (RDN1.BaseType=15) — SQLite then Neon
            var refDeliveries = _sap.GetBaseDeliveryDocEntries("RDN1", docEntry);
            foreach (var dde in refDeliveries)
            {
                var delivery = await _sap.GetDeliveryByDocEntryAsync(dde, ct);
                if (delivery != null)
                {
                    await _deliveryCache.UpsertDeliveryAsync(delivery, ct);
                    await _neonDelivery.UpsertDeliveryAsync(delivery, ct);
                }
            }

            sw.Stop();
            _log.LogInformation(
                "[ReturnHandler] Done: DocEntry={DocEntry} Items={Items} RefDeliveries={RD} {Elapsed:F1}ms EventId={EventId}",
                docEntry, itemCodes.Count, refDeliveries.Count, sw.Elapsed.TotalMilliseconds, ev.EventId);

            return (true, null);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _log.LogError(ex, "[ReturnHandler] Failed DocEntry={DocEntry} EventId={EventId} {Elapsed:F1}ms",
                ev.DocEntry, ev.EventId, sw.Elapsed.TotalMilliseconds);
            return (false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}
