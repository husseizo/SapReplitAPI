using Microsoft.EntityFrameworkCore;
using Quartz;
using SapReplitAPI.Models;
using SapReplitAPI.Services.Events;

namespace SapReplitAPI.Jobs;

/// <summary>
/// Detects and repairs stale invoice mirror entries every 15 minutes.
/// Queries OINV UpdateDate+UpdateTS for DocEntries changed since last drift check,
/// then calls IInvoiceMirrorRefresher.RefreshAsync per DocEntry (capped at MaxRepairBatch).
/// Never touches SyncMetadata["Invoice"] or any Neon bulk watermarks.
/// Schedule: 0 0/15 * * * ? (every 15 min at :00/:15/:30/:45).
/// </summary>
[DisallowConcurrentExecution]
public class InvoiceDriftDetectionJob : IJob
{
    private const string WatermarkKey  = "InvoiceDrift";
    private const int    MaxRepairBatch = 100;

    private readonly IInvoiceChangeSource _changeSource;
    private readonly IInvoiceMirrorRefresher _refresh;
    private readonly CacheDbContext _db;
    private readonly ILogger<InvoiceDriftDetectionJob> _log;

    public InvoiceDriftDetectionJob(
        IInvoiceChangeSource changeSource,
        IInvoiceMirrorRefresher refresh,
        CacheDbContext db,
        ILogger<InvoiceDriftDetectionJob> log)
    {
        _changeSource = changeSource;
        _refresh      = refresh;
        _db           = db;
        _log          = log;
    }

    public Task Execute(IJobExecutionContext context)
        => RunAsync(context.CancellationToken);

    internal async Task RunAsync(CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var meta    = await _db.SyncMetadata.AsNoTracking().FirstOrDefaultAsync(m => m.Type == WatermarkKey, ct);
            var since   = meta?.LastSyncedAt ?? DateTime.Now.AddDays(-1);

            _log.LogInformation("[INVOICE-DRIFT] START Since={Since:yyyy-MM-dd}", since);

            var changed = _changeSource.GetChangedInvoiceDocEntries(since);

            if (changed.Count == 0)
            {
                sw.Stop();
                _log.LogInformation("[INVOICE-DRIFT] DONE Changed=0 TotalMs={TotalMs:F1}", sw.Elapsed.TotalMilliseconds);
                await AdvanceWatermarkAsync(DateTime.Now, ct);
                return;
            }

            var toRepair = changed.Count > MaxRepairBatch ? changed.Take(MaxRepairBatch).ToList() : changed;
            _log.LogInformation("[INVOICE-DRIFT] Found={Found} Repairing={Repair} (cap={Cap})",
                changed.Count, toRepair.Count, MaxRepairBatch);

            int ok = 0, failed = 0;
            foreach (var docEntry in toRepair)
            {
                if (ct.IsCancellationRequested) break;
                var result = await _refresh.RefreshAsync(docEntry, ct);
                if (result.Ok) ok++;
                else
                {
                    failed++;
                    _log.LogWarning("[INVOICE-DRIFT] RepairFailed DocEntry={DocEntry} Error={Error}",
                        docEntry, result.Error);
                }
            }

            sw.Stop();
            _log.LogInformation(
                "[INVOICE-DRIFT] DONE Changed={Changed} Repaired={Ok} Failed={Failed} TotalMs={TotalMs:F1}",
                changed.Count, ok, failed, sw.Elapsed.TotalMilliseconds);

            await AdvanceWatermarkAsync(DateTime.Now, ct);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _log.LogError(ex, "[INVOICE-DRIFT] Failed after {Elapsed:F1}ms", sw.Elapsed.TotalMilliseconds);
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
