using SapReplitAPI.Services.Inventory;

namespace SapReplitAPI.Services.Events;

/// <summary>
/// Handles ObjectType=17 (ORDR), TransactionType=A/U/C.
/// SO add/update/cancel changes IsCommitted — refreshes WarehouseInventory only (no Bin, no Products).
/// Evidence label: VERIFIED (RDR1 persists after 17/C — live-confirmed DocEntry=28360).
/// Never advances SyncMetadata watermarks.
/// </summary>
public sealed class SalesOrderCommitmentEventHandler : ISapEventHandler
{
    private readonly SapService _sap;
    private readonly InventoryEventRefreshService _inv;
    private readonly ILogger<SalesOrderCommitmentEventHandler> _log;

    public SalesOrderCommitmentEventHandler(
        SapService sap,
        InventoryEventRefreshService inv,
        ILogger<SalesOrderCommitmentEventHandler> log)
    {
        _sap = sap;
        _inv = inv;
        _log = log;
    }

    public bool CanHandle(SapOutboxEvent ev)
        => ev.ObjectType == "17" && ev.TransactionType is "A" or "U" or "C";

    public async Task<(bool ok, string? error)> HandleAsync(SapOutboxEvent ev, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            if (ev.DocEntry is not { } docEntry)
            {
                _log.LogWarning("[SOCommitmentHandler] EventId={EventId} null DocEntry.", ev.EventId);
                return (false, "DocEntry is null for 17 event");
            }

            var itemCodes = _sap.GetItemCodesFromLines("RDR1", docEntry);
            if (itemCodes.Count == 0)
            {
                _log.LogInformation("[SOCommitmentHandler] DocEntry={DocEntry} TxType={Tx}: no item codes in RDR1 — skipping. EventId={EventId}",
                    docEntry, ev.TransactionType, ev.EventId);
                return (true, null);
            }

            await _inv.RefreshWarehouseInventoryAsync(itemCodes, ct);

            sw.Stop();
            _log.LogInformation(
                "[SOCommitmentHandler] Done: DocEntry={DocEntry} TxType={Tx} Items={Items} {Elapsed:F1}ms EventId={EventId}",
                docEntry, ev.TransactionType, itemCodes.Count, sw.Elapsed.TotalMilliseconds, ev.EventId);

            return (true, null);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _log.LogError(ex, "[SOCommitmentHandler] Failed DocEntry={DocEntry} TxType={Tx} EventId={EventId} {Elapsed:F1}ms",
                ev.DocEntry, ev.TransactionType, ev.EventId, sw.Elapsed.TotalMilliseconds);
            return (false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}
