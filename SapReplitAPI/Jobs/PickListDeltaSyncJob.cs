using Microsoft.EntityFrameworkCore;
using Quartz;
using SapReplitAPI.Models;
using SapReplitAPI.Services.CachedServices;
using SapReplitAPI.Services.PickList;

namespace SapReplitAPI.Jobs;

/// <summary>
/// Delta pick list sync: reads OPKL rows whose UpdateDate >= watermark − 1 day (EAT),
/// upserts to SQLite, advances the "PickList" watermark.
/// Canceled='Y' rows are included — cancellation must be reflected in cache.
/// Schedule: 0 3/5 * * * ? (every 5 min at :03/:08/:13…).
/// Never writes NeonMirror:PickLists — that is owned by NeonSyncJob.
/// </summary>
[DisallowConcurrentExecution]
public class PickListDeltaSyncJob : IJob
{
    private const string WatermarkKey = "PickList";

    private readonly PickListCacheService _cache;
    private readonly CacheDbContext _db;
    private readonly ILogger<PickListDeltaSyncJob> _log;

    public PickListDeltaSyncJob(
        PickListCacheService cache,
        CacheDbContext db,
        ILogger<PickListDeltaSyncJob> log)
    {
        _cache = cache;
        _db    = db;
        _log   = log;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        var ct = context.CancellationToken;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var meta        = await _db.SyncMetadata.AsNoTracking().FirstOrDefaultAsync(m => m.Type == WatermarkKey, ct);
            var watermarkUtc = meta?.LastSyncedAt ?? DateTime.UtcNow.AddDays(-7);

            _log.LogInformation("[PickListDelta] Watermark={Wm:u}", watermarkUtc);

            int synced = await _cache.DeltaSyncAsync(watermarkUtc, ct);

            var syncTime = DateTime.UtcNow;
            await AdvanceWatermarkAsync(syncTime, ct);

            sw.Stop();
            if (synced > 0)
                _log.LogInformation("[PickListDelta] Synced {N} pick lists in {S:F1}s.", synced, sw.Elapsed.TotalSeconds);
            else
                _log.LogDebug("[PickListDelta] No changes — watermark advanced in {S:F1}s.", sw.Elapsed.TotalSeconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _log.LogError(ex, "[PickListDelta] Failed after {S:F1}s.", sw.Elapsed.TotalSeconds);
            throw;
        }
    }

    private async Task AdvanceWatermarkAsync(DateTime at, CancellationToken ct)
    {
        var meta = await _db.SyncMetadata.FirstOrDefaultAsync(m => m.Type == WatermarkKey, ct);
        if (meta is null)
            await _db.SyncMetadata.AddAsync(new SyncMetadata { Type = WatermarkKey, LastSyncedAt = at }, ct);
        else
            meta.LastSyncedAt = at;

        await _db.SaveChangesAsync(ct);
    }
}
