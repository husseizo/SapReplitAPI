using Microsoft.EntityFrameworkCore;
using Quartz;
using SapReplitAPI.Models;
using SapReplitAPI.Services.CachedServices;

namespace SapReplitAPI.Jobs;

/// <summary>
/// Full delivery sync: reads ALL deliveries from SAP (ODLN + DLN1), upserts to SQLite,
/// advances the "Delivery" watermark.
/// Schedule: 0 30 4 * * ? EAT (04:30 EAT daily), DoNothing misfire.
/// Never writes NeonMirror:Deliveries — that is owned by NeonSyncJob.
/// </summary>
[DisallowConcurrentExecution]
public class DeliveryFullSyncJob : IJob
{
    private readonly SapService _sap;
    private readonly DeliveryCacheService _cache;
    private readonly CacheDbContext _db;
    private readonly ILogger<DeliveryFullSyncJob> _log;

    public DeliveryFullSyncJob(
        SapService sap,
        DeliveryCacheService cache,
        CacheDbContext db,
        ILogger<DeliveryFullSyncJob> log)
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
        _log.LogInformation("[DeliveryFullSync] Started.");

        try
        {
            // Read all ODLN headers from SAP (no date filter = full)
            var headers = _sap.GetDeliveryHeaders(from: null, to: null);
            _log.LogInformation("[DeliveryFullSync] SAP returned {Count} ODLN headers.", headers.Count);

            if (headers.Count == 0)
            {
                _log.LogWarning("[DeliveryFullSync] SAP returned 0 headers — skipping watermark advance.");
                return;
            }

            // Batch-fetch DLN1 lines for all DocEntries
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

            // Advance "Delivery" watermark
            var syncTime = DateTime.UtcNow;
            await AdvanceWatermarkAsync(syncTime, ct);

            sw.Stop();
            _log.LogInformation("[DeliveryFullSync] Done: {U} upserted in {S:F1}s.", upserted, sw.Elapsed.TotalSeconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _log.LogError(ex, "[DeliveryFullSync] Failed after {S:F1}s.", sw.Elapsed.TotalSeconds);
            throw;
        }
    }

    private async Task AdvanceWatermarkAsync(DateTime at, CancellationToken ct)
    {
        const string Key = "Delivery";
        var meta = await _db.SyncMetadata.FirstOrDefaultAsync(m => m.Type == Key, ct);
        if (meta is null)
            await _db.SyncMetadata.AddAsync(new SyncMetadata { Type = Key, LastSyncedAt = at }, ct);
        else
            meta.LastSyncedAt = at;

        await _db.SaveChangesAsync(ct);
    }
}
