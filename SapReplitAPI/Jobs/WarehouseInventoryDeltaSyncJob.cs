using Quartz;
using SapReplitAPI.Services.Inventory;
using System.Diagnostics;

namespace SapReplitAPI.Jobs;

[DisallowConcurrentExecution]
public class WarehouseInventoryDeltaSyncJob : IJob
{
    private readonly WarehouseInventorySyncService _sync;
    private readonly ILogger<WarehouseInventoryDeltaSyncJob> _log;

    public WarehouseInventoryDeltaSyncJob(
        WarehouseInventorySyncService sync,
        ILogger<WarehouseInventoryDeltaSyncJob> log)
    {
        _sync = sync;
        _log  = log;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        var sw = Stopwatch.StartNew();
        _log.LogInformation("[WHInv][DeltaJob] Triggered at {Time}", DateTime.Now);
        try
        {
            var (upserted, removed) = await _sync.DeltaSyncAsync();
            sw.Stop();
            _log.LogInformation(
                "[WHInv][DeltaJob] Completed — upserted: {U} | removed: {R} | duration: {S:F1}s",
                upserted, removed, sw.Elapsed.TotalSeconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _log.LogError(ex,
                "[WHInv][DeltaJob] Failed after {S:F1}s", sw.Elapsed.TotalSeconds);
            throw;
        }
    }
}
