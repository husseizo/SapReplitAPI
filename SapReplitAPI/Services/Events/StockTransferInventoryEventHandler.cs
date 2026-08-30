using SapReplitAPI.Services.Inventory;

namespace SapReplitAPI.Services.Events;

/// <summary>
/// Handles ObjectType=67 (OWTR / Stock Transfer), TransactionType=A or C.
/// Moves stock between warehouses — refreshes both source and destination.
/// Evidence label: VERIFIED (WTR1 persists after 67/C — live-confirmed DocEntry=5792).
/// Never advances SyncMetadata watermarks.
/// </summary>
public sealed class StockTransferInventoryEventHandler : ISapEventHandler
{
    private readonly SapService _sap;
    private readonly InventoryEventRefreshService _inv;
    private readonly ILogger<StockTransferInventoryEventHandler> _log;

    public StockTransferInventoryEventHandler(
        SapService sap,
        InventoryEventRefreshService inv,
        ILogger<StockTransferInventoryEventHandler> log)
    {
        _sap = sap;
        _inv = inv;
        _log = log;
    }

    public bool CanHandle(SapOutboxEvent ev)
        => ev.ObjectType == "67" && ev.TransactionType is "A" or "C";

    public async Task<(bool ok, string? error)> HandleAsync(SapOutboxEvent ev, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            if (ev.DocEntry is not { } docEntry)
            {
                _log.LogWarning("[STHandler] EventId={EventId} null DocEntry.", ev.EventId);
                return (false, "DocEntry is null for 67 event");
            }

            var itemCodes = _sap.GetItemCodesFromLines("WTR1", docEntry);
            if (itemCodes.Count == 0)
            {
                _log.LogInformation("[STHandler] DocEntry={DocEntry} TxType={Tx}: no item codes in WTR1. EventId={EventId}",
                    docEntry, ev.TransactionType, ev.EventId);
                return (true, null);
            }

            await _inv.RefreshFullInventoryAsync(itemCodes, ct);

            sw.Stop();
            _log.LogInformation("[STHandler] Done: DocEntry={DocEntry} TxType={Tx} Items={Items} {Elapsed:F1}ms EventId={EventId}",
                docEntry, ev.TransactionType, itemCodes.Count, sw.Elapsed.TotalMilliseconds, ev.EventId);

            return (true, null);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _log.LogError(ex, "[STHandler] Failed DocEntry={DocEntry} TxType={Tx} EventId={EventId} {Elapsed:F1}ms",
                ev.DocEntry, ev.TransactionType, ev.EventId, sw.Elapsed.TotalMilliseconds);
            return (false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}
