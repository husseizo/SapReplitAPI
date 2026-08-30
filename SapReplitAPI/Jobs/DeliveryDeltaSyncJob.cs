using Microsoft.EntityFrameworkCore;
using Quartz;
using SapReplitAPI.Models;
using SapReplitAPI.Services.CachedServices;

namespace SapReplitAPI.Jobs;

/// <summary>
/// Delta delivery sync: reads ODLN changed since the last "Delivery" watermark,
/// upserts to SQLite, advances the watermark.
/// Schedule: 0 1/5 * * * ? (every 5 min at :01/:06/:11…).
/// Never writes NeonMirror:Deliveries — that is owned by NeonSyncJob.
/// </summary>
[DisallowConcurrentExecution]
public class DeliveryDeltaSyncJob : IJob
{
    private static readonly TimeSpan LookbackWindow = TimeSpan.FromHours(2);
    private const string WatermarkKey = "Delivery";

    private readonly SapService _sap;
    private readonly DeliveryCacheService _cache;
    private readonly CacheDbContext _db;
    private readonly ILogger<DeliveryDeltaSyncJob> _log;

    public DeliveryDeltaSyncJob(
        SapService sap,
        DeliveryCacheService cache,
        CacheDbContext db,
        ILogger<DeliveryDeltaSyncJob> log)
    {
        _sap   = sap;
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
            var lastSync    = meta?.LastSyncedAt ?? DateTime.UtcNow.AddDays(-7);
            var effectiveFrom = lastSync - LookbackWindow;

            _log.LogInformation("[DeliveryDelta] From={From} (watermark {Last} - 2h).",
                effectiveFrom.ToString("yyyy-MM-dd HH:mm:ss"), lastSync.ToString("yyyy-MM-dd HH:mm:ss"));

            var headers = _sap.GetDeliveryHeaders(from: effectiveFrom, to: null);

            if (headers.Count == 0)
            {
                _log.LogDebug("[DeliveryDelta] No changed deliveries — watermark advanced.");
                await AdvanceWatermarkAsync(DateTime.UtcNow, ct);
                return;
            }

            var docEntries = headers.Select(h => h.DocEntry).ToList();
            var allLines   = _sap.GetDeliveryLines(docEntries);

            var linesByDoc = allLines
                .GroupBy(l => l.DocEntry)
                .ToDictionary(g => g.Key, g => g.ToList());

            int upserted = 0;
            foreach (var header in headers)
            {
                if (linesByDoc.TryGetValue(header.DocEntry, out var lines))
                    header.Lines = lines;

                await _cache.UpsertDeliveryAsync(header, ct);
                upserted++;
            }

            var syncTime = DateTime.UtcNow;
            await AdvanceWatermarkAsync(syncTime, ct);

            sw.Stop();
            _log.LogInformation("[DeliveryDelta] Done: {U} upserted in {S:F1}s.", upserted, sw.Elapsed.TotalSeconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _log.LogError(ex, "[DeliveryDelta] Failed after {S:F1}s.", sw.Elapsed.TotalSeconds);
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
