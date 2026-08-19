using Quartz;
using SapReplitAPI.Services.Inventory;
using System.Diagnostics;

namespace SapReplitAPI.Jobs;

[DisallowConcurrentExecution]
public class BinInventoryDeltaSyncJob : IJob
{
    private readonly BinInventorySyncService _sync;
    private readonly ILogger<BinInventoryDeltaSyncJob> _log;

    public BinInventoryDeltaSyncJob(
        BinInventorySyncService sync,
        ILogger<BinInventoryDeltaSyncJob> log)
    {
        _sync = sync;
        _log  = log;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        var sw = Stopwatch.StartNew();
        _log.LogInformation("[BinInv][DeltaJob] Triggered at {Time}", DateTime.Now);
        try
        {
            var (upserted, removed) = await _sync.DeltaSyncAsync();
            sw.Stop();
            _log.LogInformation(
                "[BinInv][DeltaJob] Completed — upserted: {U} | removed: {R} | duration: {S:F1}s",
                upserted, removed, sw.Elapsed.TotalSeconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _log.LogError(ex,
                "[BinInv][DeltaJob] Failed after {S:F1}s", sw.Elapsed.TotalSeconds);
            throw;
        }
    }
}
