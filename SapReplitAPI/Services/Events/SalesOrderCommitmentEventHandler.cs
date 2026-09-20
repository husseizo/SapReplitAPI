using Microsoft.EntityFrameworkCore;
using SapReplitAPI.Services.Inventory;
using SapReplitAPI.Services.PickList;
using SapReplitAPI.Services.TodayOrders;
using SapReplitAPI.Services.ZoneFulfillment;

namespace SapReplitAPI.Services.Events;

/// <summary>
/// Handles ObjectType=17 (ORDR), TransactionType=A/U/C.
/// SO add/update/cancel:
///   1. Refreshes WarehouseInventory (IsCommitted changes).
///   2. Refreshes TodayOrders cache (SQLite + Neon) via event-driven fast path.
///   3. On 17/U: reconciles ZF divergence caused by SAP GUI edits.
///      AMBER (zero physical picks) → controlled replan via coordinator; new OPKLs refreshed.
///      RED (physical picks > 0)    → logs ZF_ORDER_EDIT_CONFLICT; delivery gate blocks.
///      RecoveryRequired            → returns (false, error) so outbox retries.
///      No rebuild loop: after replan the fragment matches RDR1 — next 17/U finds no divergence.
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
    private readonly IZoneFulfillmentOrderEditCoordinator _coordinator;
    private readonly ILogger<SalesOrderCommitmentEventHandler> _log;

    public SalesOrderCommitmentEventHandler(
        SapService sap,
        InventoryEventRefreshService inv,
        TodayOrderEventRefreshService todayRefresh,
        CacheDbContext sqlite,
        IPickListEventRefreshService plRefresh,
        IZoneFulfillmentOrderEditCoordinator coordinator,
        ILogger<SalesOrderCommitmentEventHandler> log)
    {
        _sap          = sap;
        _inv          = inv;
        _todayRefresh = todayRefresh;
        _sqlite       = sqlite;
        _plRefresh    = plRefresh;
        _coordinator  = coordinator;
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

            // ── 3. ZF reconcile + PickList refresh (17/U only) ──────────────
            if (ev.TransactionType == "U")
            {
                // Reconcile SAP GUI edits vs ZF fragment state.
                // AMBER path: retires old OPKLs and creates replacements — idempotent (no rebuild loop).
                // RED path: logs conflict; delivery gate already blocks ODLN.
                // RecoveryRequired: return (false, error) so outbox retries.
                var (recoveryRequired, replanError, newAbsEntries) =
                    await ReconcileOrLogAsync(docEntry, ev.EventId.ToString(), ct);

                if (recoveryRequired)
                {
                    sw.Stop();
                    return (false, replanError);
                }

                // Refresh new OPKLs if replan created them; otherwise refresh existing cached OPKLs.
                List<int> refreshTargets;
                if (newAbsEntries.Count > 0)
                {
                    refreshTargets = newAbsEntries;
                }
                else
                {
                    refreshTargets = await _sqlite.PickListLines.AsNoTracking()
                        .Where(l => l.OrderEntry == docEntry)
                        .Select(l => l.AbsEntry)
                        .Distinct()
                        .ToListAsync(ct);
                }

                foreach (var absEntry in refreshTargets)
                {
                    try { await _plRefresh.RefreshAsync(absEntry, ct); }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex,
                            "[SOCommitmentHandler] PickList cache fast-path non-fatal AbsEntry={Abs} DocEntry={DocEntry} EventId={EventId}",
                            absEntry, docEntry, ev.EventId);
                    }
                }

                if (refreshTargets.Count > 0)
                    _log.LogInformation(
                        "[SOCommitmentHandler] PickList refresh triggered AbsEntries={N} DocEntry={DocEntry} EventId={EventId}",
                        refreshTargets.Count, docEntry, ev.EventId);
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

    // ── ZF reconcile (17/U) ──────────────────────────────────────────────────

    /// <summary>
    /// Delegates to coordinator.ReconcileExternalSapEditAsync.
    /// Returns (recoveryRequired, error, newAbsEntries).
    /// Non-fatal for Blocked/NotZf/Success — only RecoveryRequired propagates as event failure.
    /// </summary>
    private async Task<(bool recoveryRequired, string? error, List<int> newAbsEntries)> ReconcileOrLogAsync(
        int docEntry, string eventId, CancellationToken ct)
    {
        try
        {
            var result = await _coordinator.ReconcileExternalSapEditAsync(docEntry, eventId, "sap-gui", ct);

            if (result.IsNotZf)
                return (false, null, []);

            if (result.IsBlocked)
            {
                _log.LogError(
                    "[SOCommitmentHandler] ZF_ORDER_EDIT_CONFLICT DocEntry={Doc} Code={Code} " +
                    "SAP GUI edit blocked (physical pick or active delivery). EventId={EventId}",
                    docEntry, result.BlockCode, eventId);
                return (false, null, []);
            }

            if (result.IsRecoveryRequired)
            {
                _log.LogError(
                    "[SOCommitmentHandler] ZF_EXTERNAL_REPLAN_RECOVERY DocEntry={Doc} " +
                    "OperationId={OpId} Code={Code} EventId={EventId}",
                    docEntry, result.ReplanOperationId, result.BlockCode, eventId);
                return (true, $"ZF external replan recovery required: {result.BlockReason}", []);
            }

            if (result.IsSuccess && result.WasAmber)
                _log.LogInformation(
                    "[SOCommitmentHandler] ZF_EXTERNAL_REPLAN_COMPLETE DocEntry={Doc} " +
                    "OperationId={OpId} newOPKLs={N} EventId={EventId}",
                    docEntry, result.ReplanOperationId, result.NewPickListAbsEntries.Count, eventId);

            return (false, null, result.NewPickListAbsEntries.ToList());
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "[SOCommitmentHandler] ZF reconcile non-fatal DocEntry={Doc} EventId={EventId}",
                docEntry, eventId);
            return (false, null, []);
        }
    }
}
