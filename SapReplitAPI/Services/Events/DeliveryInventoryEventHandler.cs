using SapReplitAPI.Services.CachedServices;
using SapReplitAPI.Services.Inventory;
using SapReplitAPI.Services.Neon;

namespace SapReplitAPI.Services.Events;

/// <summary>
/// Handles ObjectType=15 (ODLN), TransactionType=A.
/// Updates Delivery cache (SQLite + Neon targeted write) + full inventory refresh.
/// Cancellation (Canceled='C'): also refreshes the original delivery in SQLite + Neon.
/// Evidence label: VERIFIED (ODLN cancellation semantics confirmed — 4,165 pairs in production).
/// Never advances SyncMetadata watermarks.
/// </summary>
public sealed class DeliveryInventoryEventHandler : ISapEventHandler
{
    private readonly SapService _sap;
    private readonly DeliveryCacheService _deliveryCache;
    private readonly NeonDeliveryWriteService _neonDelivery;
    private readonly InventoryEventRefreshService _inv;
    private readonly ILogger<DeliveryInventoryEventHandler> _log;

    public DeliveryInventoryEventHandler(
        SapService sap,
        DeliveryCacheService deliveryCache,
        NeonDeliveryWriteService neonDelivery,
        InventoryEventRefreshService inv,
        ILogger<DeliveryInventoryEventHandler> log)
    {
        _sap           = sap;
        _deliveryCache = deliveryCache;
        _neonDelivery  = neonDelivery;
        _inv           = inv;
        _log           = log;
    }

    public bool CanHandle(SapOutboxEvent ev)
        => ev.ObjectType == "15" && ev.TransactionType == "A";

    public async Task<(bool ok, string? error)> HandleAsync(SapOutboxEvent ev, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            if (ev.DocEntry is not { } docEntry)
            {
                _log.LogWarning("[DeliveryHandler] EventId={EventId} null DocEntry.", ev.EventId);
                return (false, "DocEntry is null for 15/A event");
            }

            // 1. Read fresh delivery from SAP
            var delivery = await _sap.GetDeliveryByDocEntryAsync(docEntry, ct);
            if (delivery == null)
            {
                _log.LogWarning("[DeliveryHandler] DocEntry={DocEntry} not found in ODLN. EventId={EventId}", docEntry, ev.EventId);
                return (false, $"Delivery DocEntry={docEntry} not found in SAP");
            }

            // 2. Update Delivery cache — SQLite then Neon (fast path, no watermark written)
            await _deliveryCache.UpsertDeliveryAsync(delivery, ct);
            await _neonDelivery.UpsertDeliveryAsync(delivery, ct);

            // 3. If Canceled='C': also refresh the original delivery in SQLite + Neon
            if (delivery.Canceled == "C")
            {
                var originalDocEntry = await _sap.GetOriginalDeliveryDocEntryAsync(docEntry, ct);
                if (originalDocEntry.HasValue)
                {
                    var original = await _sap.GetDeliveryByDocEntryAsync(originalDocEntry.Value, ct);
                    if (original != null)
                    {
                        await _deliveryCache.UpsertDeliveryAsync(original, ct);
                        await _neonDelivery.UpsertDeliveryAsync(original, ct);
                        _log.LogInformation("[DeliveryHandler] CancellationPair: cancellation={DE} original={Orig} EventId={EventId}",
                            docEntry, originalDocEntry.Value, ev.EventId);
                    }
                }
            }

            // 4. Inventory refresh (physical stock movement)
            var itemCodes = delivery.Lines
                .Select(l => l.ItemCode)
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (itemCodes.Count > 0)
                await _inv.RefreshFullInventoryAsync(itemCodes, ct);

            sw.Stop();
            _log.LogInformation(
                "[DeliveryHandler] Done: DocEntry={DocEntry} DocNum={DocNum} Status={Status} Canceled={Can} " +
                "U_ReplitId={ReplitId} Items={Items} {Elapsed:F1}ms EventId={EventId}",
                delivery.DocEntry, delivery.DocNum, delivery.DocStatus, delivery.Canceled,
                delivery.U_ReplitId, itemCodes.Count, sw.Elapsed.TotalMilliseconds, ev.EventId);

            return (true, null);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _log.LogError(ex, "[DeliveryHandler] Failed DocEntry={DocEntry} EventId={EventId} {Elapsed:F1}ms",
                ev.DocEntry, ev.EventId, sw.Elapsed.TotalMilliseconds);
            return (false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}
