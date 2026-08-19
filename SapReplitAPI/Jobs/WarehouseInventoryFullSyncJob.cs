using Quartz;
using SapReplitAPI.Services.Inventory;
using System.Diagnostics;

namespace SapReplitAPI.Jobs;

[DisallowConcurrentExecution]
public class WarehouseInventoryFullSyncJob : IJob
{
    private readonly WarehouseInventorySyncService _sync;
    private readonly ILogger<WarehouseInventoryFullSyncJob> _log;

    public WarehouseInventoryFullSyncJob(
        WarehouseInventorySyncService sync,
        ILogger<WarehouseInventoryFullSyncJob> log)
    {
        _sync = sync;
        _log  = log;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        var sw = Stopwatch.StartNew();
        _log.LogInformation("[WHInv][FullJob] Triggered at {Time}", DateTime.Now);
        try
        {
            var (upserted, removed) = await _sync.FullSyncAsync();
            sw.Stop();
            _log.LogInformation(
                "[WHInv][FullJob] Completed — upserted: {U} | removed: {R} | duration: {S:F1}s",
                upserted, removed, sw.Elapsed.TotalSeconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _log.LogError(ex,
                "[WHInv][FullJob] Failed after {S:F1}s", sw.Elapsed.TotalSeconds);
            throw;
        }
    }
}
