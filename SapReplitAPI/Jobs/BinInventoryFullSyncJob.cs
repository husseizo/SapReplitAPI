using Quartz;
using SapReplitAPI.Services.Inventory;
using System.Diagnostics;

namespace SapReplitAPI.Jobs;

[DisallowConcurrentExecution]
public class BinInventoryFullSyncJob : IJob
{
    private readonly BinInventorySyncService _sync;
    private readonly ILogger<BinInventoryFullSyncJob> _log;

    public BinInventoryFullSyncJob(
        BinInventorySyncService sync,
        ILogger<BinInventoryFullSyncJob> log)
    {
        _sync = sync;
        _log  = log;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        var sw = Stopwatch.StartNew();
        _log.LogInformation("[BinInv][FullJob] Triggered at {Time}", DateTime.Now);
        try
        {
            var report = await _sync.FullSyncAsync();
            sw.Stop();
            _log.LogInformation(
                "[BinInv][FullJob] Completed — rows: {Rows} | upserted: {U} | removed: {R} | duration: {S:F1}s",
                report.TotalSapRows, report.Upserted, report.Removed, sw.Elapsed.TotalSeconds);
            _log.LogInformation(
                "[BinInv][FullJob] Reconciliation — checked: {C} | matched: {M} | mismatched: {MM}",
                report.Reconciliation.Checked, report.Reconciliation.Matched, report.Reconciliation.Mismatched);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _log.LogError(ex,
                "[BinInv][FullJob] Failed after {S:F1}s", sw.Elapsed.TotalSeconds);
            throw;
        }
    }
}
