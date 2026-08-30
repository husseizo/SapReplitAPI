using SapReplitAPI.Services.Inventory;

namespace SapReplitAPI.Services.Events;

/// <summary>
/// Handles ObjectType=60 (OIGE / Goods Issue), TransactionType=A.
/// Decreases physical stock.
/// Evidence label: EXPECTED. Do NOT add 60/C.
/// Never advances SyncMetadata watermarks.
/// </summary>
public sealed class GoodsIssueInventoryEventHandler : ISapEventHandler
{
    private readonly SapService _sap;
    private readonly InventoryEventRefreshService _inv;
    private readonly ILogger<GoodsIssueInventoryEventHandler> _log;

    public GoodsIssueInventoryEventHandler(
        SapService sap,
        InventoryEventRefreshService inv,
        ILogger<GoodsIssueInventoryEventHandler> log)
    {
        _sap = sap;
        _inv = inv;
        _log = log;
    }

    public bool CanHandle(SapOutboxEvent ev)
        => ev.ObjectType == "60" && ev.TransactionType == "A";

    public async Task<(bool ok, string? error)> HandleAsync(SapOutboxEvent ev, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            if (ev.DocEntry is not { } docEntry)
            {
                _log.LogWarning("[GIHandler] EventId={EventId} null DocEntry.", ev.EventId);
                return (false, "DocEntry is null for 60/A event");
            }

            var itemCodes = _sap.GetItemCodesFromLines("IGE1", docEntry);
            if (itemCodes.Count == 0)
            {
                _log.LogInformation("[GIHandler] DocEntry={DocEntry}: no item codes in IGE1. EventId={EventId}", docEntry, ev.EventId);
                return (true, null);
            }

            await _inv.RefreshFullInventoryAsync(itemCodes, ct);

            sw.Stop();
            _log.LogInformation("[GIHandler] Done: DocEntry={DocEntry} Items={Items} {Elapsed:F1}ms EventId={EventId}",
                docEntry, itemCodes.Count, sw.Elapsed.TotalMilliseconds, ev.EventId);

            return (true, null);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _log.LogError(ex, "[GIHandler] Failed DocEntry={DocEntry} EventId={EventId} {Elapsed:F1}ms",
                ev.DocEntry, ev.EventId, sw.Elapsed.TotalMilliseconds);
            return (false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}
