using Microsoft.EntityFrameworkCore;

namespace SapReplitAPI.Services.PickList;

/// <summary>
/// Detects externally-made SAP picks (Warehouse App, SAP GUI) by comparing
/// OPKL SAP state against the SQLite cache for non-terminal pick lists.
/// When a change is found, delegates to IPickListEventRefreshService.RefreshAsync(absEntry)
/// to write SQLite + Neon atomically, per-AbsEntry.
///
/// PICK-LIST STATUS MIRRORING IS PER PICK LIST.
/// DELIVERY AUTOMATION IS PER ORCHESTRATION.
/// This service does NOT acquire ZoneFulfillmentDeliveryCoordinator.
/// This service does NOT call EvaluateAndTriggerDeliveryAsync.
/// </summary>
public sealed class PickListMirrorFreshnessService
{
    private readonly IPickListSapHeaderReader          _sapReader;
    private readonly CacheDbContext                    _db;
    private readonly IPickListEventRefreshService      _refresh;
    private readonly ILogger<PickListMirrorFreshnessService> _log;

    public PickListMirrorFreshnessService(
        IPickListSapHeaderReader               sapReader,
        CacheDbContext                         db,
        IPickListEventRefreshService           refresh,
        ILogger<PickListMirrorFreshnessService> log)
    {
        _sapReader = sapReader;
        _db        = db;
        _refresh   = refresh;
        _log       = log;
    }

    /// <summary>
    /// Scans non-terminal SQLite pick lists (Status not Picked/Closed, not Canceled),
    /// detects SAP state changes via a per-AbsEntry SAP header read, and calls
    /// RefreshAsync for each changed pick list.
    /// Per-AbsEntry isolation: one failure does not block siblings.
    /// </summary>
    public async Task RefreshNonTerminalAsync(CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;

        var candidates = await _db.PickLists
            .AsNoTracking()
            .Where(p => p.Canceled != "Y" && p.Status != "Y" && p.Status != "C")
            .Select(p => new { p.AbsEntry, p.UpdateDate, p.Status, p.Canceled })
            .ToListAsync(ct);

        if (candidates.Count == 0)
        {
            _log.LogDebug("[PLMirrorFreshness] No non-terminal pick lists in cache.");
            return;
        }

        _log.LogDebug("[PLMirrorFreshness] Checking {N} non-terminal pick list(s)", candidates.Count);

        int refreshed = 0, skipped = 0, errors = 0;

        foreach (var cached in candidates)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var sapHeader = _sapReader.GetPickListHeaderByAbsEntry(cached.AbsEntry);

                if (sapHeader == null)
                {
                    _log.LogDebug(
                        "[PLMirrorFreshness] AbsEntry={Abs} not found in SAP — skipping",
                        cached.AbsEntry);
                    skipped++;
                    continue;
                }

                bool changed =
                    sapHeader.UpdateDate > cached.UpdateDate ||
                    sapHeader.Status     != cached.Status    ||
                    sapHeader.Canceled   != cached.Canceled;

                if (!changed)
                {
                    skipped++;
                    continue;
                }

                _log.LogInformation(
                    "[PLMirrorFreshness] Change detected AbsEntry={Abs} " +
                    "CachedUpdateDate={CUd:d} SapUpdateDate={SUd:d} " +
                    "CachedStatus={CSt} SapStatus={SSt} CachedCanceled={CCa} SapCanceled={SCa}",
                    cached.AbsEntry,
                    cached.UpdateDate, sapHeader.UpdateDate,
                    cached.Status, sapHeader.Status,
                    cached.Canceled, sapHeader.Canceled);

                var (ok, error) = await _refresh.RefreshAsync(cached.AbsEntry, ct);
                if (ok)
                    refreshed++;
                else
                {
                    _log.LogWarning(
                        "[PLMirrorFreshness] RefreshAsync failed AbsEntry={Abs} Error={Err}",
                        cached.AbsEntry, error);
                    errors++;
                }
            }
            catch (Exception ex)
            {
                errors++;
                _log.LogError(ex,
                    "[PLMirrorFreshness] Unhandled error for AbsEntry={Abs} — skipping to next",
                    cached.AbsEntry);
            }
        }

        if (refreshed > 0 || errors > 0)
            _log.LogInformation(
                "[PLMirrorFreshness] Cycle complete — Checked={N} Refreshed={R} Skipped={S} Errors={E}",
                candidates.Count, refreshed, skipped, errors);
        else
            _log.LogDebug(
                "[PLMirrorFreshness] Cycle complete — Checked={N} no changes",
                candidates.Count);
    }
}
