using Microsoft.EntityFrameworkCore;
using Quartz;
using SapReplitAPI.Models;
using SapReplitAPI.Services.CachedServices;
using SapReplitAPI.Services.PickList;

namespace SapReplitAPI.Jobs;

/// <summary>
/// Full pick list sync: reads ALL OPKL rows from SAP and upserts to SQLite.
/// Advances the "PickList" watermark after a successful commit.
/// Schedule: 0 45 5 * * ? EAT (05:45 EAT daily), DoNothing misfire.
/// Never writes NeonMirror:PickLists — that is owned by NeonSyncJob.
/// </summary>
[DisallowConcurrentExecution]
public class PickListFullSyncJob : IJob
{
    private const string WatermarkKey = "PickList";

    private readonly PickListCacheService _cache;
    private readonly CacheDbContext _db;
    private readonly ILogger<PickListFullSyncJob> _log;

    public PickListFullSyncJob(
        PickListCacheService cache,
        CacheDbContext db,
        ILogger<PickListFullSyncJob> log)
    {
        _cache = cache;
        _db    = db;
        _log   = log;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        var ct = context.CancellationToken;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _log.LogInformation("[PickListFullSync] Started.");

        try
        {
            await _cache.FullSyncAsync(ct);

            var syncTime = DateTime.UtcNow;
            await AdvanceWatermarkAsync(syncTime, ct);

            sw.Stop();
            _log.LogInformation("[PickListFullSync] Done in {S:F1}s.", sw.Elapsed.TotalSeconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _log.LogError(ex, "[PickListFullSync] Failed after {S:F1}s.", sw.Elapsed.TotalSeconds);
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
