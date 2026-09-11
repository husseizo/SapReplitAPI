using Microsoft.EntityFrameworkCore;
using SapReplitAPI.Services.Inventory;
using SapReplitAPI.Services.PickList;
using SapReplitAPI.Services.TodayOrders;

namespace SapReplitAPI.Services.Events;

/// <summary>
/// Handles ObjectType=17 (ORDR), TransactionType=A/U/C.
/// SO add/update/cancel:
///   1. Refreshes WarehouseInventory (IsCommitted changes).
///   2. Refreshes TodayOrders cache (SQLite + Neon) via event-driven fast path.
///      Expected freshness: seconds after SAP ORDR commit.
/// Never advances SyncMetadata watermarks directly — delegates to respective services.
/// Evidence label: VERIFIED (RDR1 persists after 17/C — live-confirmed DocEntry=28360).
/// </summary>
public sealed class SalesOrderCommitmentEventHandler : ISapEventHandler
{
    private readonly SapService _sap;
    private readonly InventoryEventRefreshService _inv;
    private readonly TodayOrderEventRefreshService _todayRefresh;
    private readonly CacheDbContext _sqlite;
    private readonly IPickListEventRefreshService _plRefresh;
    private readonly ILogger<SalesOrderCommitmentEventHandler> _log;

    public SalesOrderCommitmentEventHandler(
        SapService sap,
        InventoryEventRefreshService inv,
        TodayOrderEventRefreshService todayRefresh,
        CacheDbContext sqlite,
        IPickListEventRefreshService plRefresh,
        ILogger<SalesOrderCommitmentEventHandler> log)
    {
        _sap          = sap;
        _inv          = inv;
        _todayRefresh = todayRefresh;
        _sqlite       = sqlite;
        _plRefresh    = plRefresh;
        _log          = log;
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

            // ── 1. WarehouseInventory refresh (commitment change) ────────────
            var itemCodes = _sap.GetItemCodesFromLines("RDR1", docEntry);
            if (itemCodes.Count == 0)
            {
                _log.LogInformation("[SOCommitmentHandler] DocEntry={DocEntry} TxType={Tx}: no item codes in RDR1 — skipping inventory. EventId={EventId}",
                    docEntry, ev.TransactionType, ev.EventId);
            }
            else
            {
                await _inv.RefreshWarehouseInventoryAsync(itemCodes, ct);
            }

            // ── 2. TodayOrders fast path (SQLite + Neon) ────────────────────
            var (todayOk, todayError) = await _todayRefresh.RefreshAsync(docEntry, ev.TransactionType!, ct);
            if (!todayOk)
            {
                sw.Stop();
                _log.LogError("[SOCommitmentHandler] TodayOrders refresh failed. DocEntry={DocEntry} TxType={Tx} Error={Err} EventId={EventId}",
                    docEntry, ev.TransactionType, todayError, ev.EventId);
                return (false, $"TodayOrders: {todayError}");
            }

            // ── 3. PickList fast path (SQLite + Neon + PLR + PLFR) ─────────────
            // Only on U: an ORDR update (e.g. WhsCode change) may invalidate
            // released OPKLs against this SO. A and C do not affect existing picks.
            if (ev.TransactionType == "U")
            {
                var absEntries = await _sqlite.PickListLines.AsNoTracking()
                    .Where(l => l.OrderEntry == docEntry)
                    .Select(l => l.AbsEntry)
                    .Distinct()
                    .ToListAsync(ct);

                foreach (var absEntry in absEntries)
                {
                    try { await _plRefresh.RefreshAsync(absEntry, ct); }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "[SOCommitmentHandler] PickList cache fast-path non-fatal AbsEntry={Abs} DocEntry={DocEntry} EventId={EventId}",
                            absEntry, docEntry, ev.EventId);
                    }
                }

                if (absEntries.Count > 0)
                    _log.LogInformation("[SOCommitmentHandler] PickList refresh triggered AbsEntries={N} DocEntry={DocEntry} EventId={EventId}",
                        absEntries.Count, docEntry, ev.EventId);
            }

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
