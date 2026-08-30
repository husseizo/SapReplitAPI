using SapReplitAPI.Services.Inventory;

namespace SapReplitAPI.Services.Events;

/// <summary>
/// Handles ObjectType=20 (OPDN), TransactionType=A ONLY.
/// Goods Receipt PO increases physical stock.
/// Evidence label: CANDIDATE — needs controlled test to confirm ObjectType=20/TransactionType=A in outbox.
/// Note: PDN1.Dscription column name is SAP typo (confirmed in schema).
/// Never advances SyncMetadata watermarks. Do NOT add 20/C.
/// </summary>
public sealed class GoodsReceiptPoEventHandler : ISapEventHandler
{
    private readonly SapService _sap;
    private readonly InventoryEventRefreshService _inv;
    private readonly ILogger<GoodsReceiptPoEventHandler> _log;

    public GoodsReceiptPoEventHandler(
        SapService sap,
        InventoryEventRefreshService inv,
        ILogger<GoodsReceiptPoEventHandler> log)
    {
        _sap = sap;
        _inv = inv;
        _log = log;
    }

    public bool CanHandle(SapOutboxEvent ev)
        => ev.ObjectType == "20" && ev.TransactionType == "A";

    public async Task<(bool ok, string? error)> HandleAsync(SapOutboxEvent ev, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            if (ev.DocEntry is not { } docEntry)
            {
                _log.LogWarning("[GRPOHandler] EventId={EventId} null DocEntry.", ev.EventId);
                return (false, "DocEntry is null for 20/A event");
            }

            var itemCodes = _sap.GetItemCodesFromLines("PDN1", docEntry);
            if (itemCodes.Count == 0)
            {
                _log.LogInformation("[GRPOHandler] DocEntry={DocEntry}: no item codes in PDN1. EventId={EventId}", docEntry, ev.EventId);
                return (true, null);
            }

            await _inv.RefreshFullInventoryAsync(itemCodes, ct);

            sw.Stop();
            _log.LogInformation("[GRPOHandler] Done: DocEntry={DocEntry} Items={Items} {Elapsed:F1}ms EventId={EventId}",
                docEntry, itemCodes.Count, sw.Elapsed.TotalMilliseconds, ev.EventId);

            return (true, null);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _log.LogError(ex, "[GRPOHandler] Failed DocEntry={DocEntry} EventId={EventId} {Elapsed:F1}ms",
                ev.DocEntry, ev.EventId, sw.Elapsed.TotalMilliseconds);
            return (false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}
