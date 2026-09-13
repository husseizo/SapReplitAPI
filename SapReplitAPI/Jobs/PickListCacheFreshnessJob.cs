using Quartz;
using SapReplitAPI.Services.PickList;

namespace SapReplitAPI.Jobs;

/// <summary>
/// Quartz job — fires every 30 seconds.
/// Detects externally-made SAP picks (Warehouse App, SAP GUI) that bypass the ZF API,
/// compares OPKL SAP state against the SQLite cache for non-terminal pick lists,
/// and propagates changes to SQLite + Neon per-AbsEntry via IPickListEventRefreshService.
///
/// PICK-LIST STATUS MIRRORING IS PER PICK LIST.
/// Does NOT acquire ZoneFulfillmentDeliveryCoordinator.
/// Does NOT call EvaluateAndTriggerDeliveryAsync.
/// Delivery automation remains the sole responsibility of ZoneFulfillmentPickReconciliationJob.
/// The system remains correct if the Warehouse App NEVER calls the optional hook
/// (POST /internal/refresh/pick-list/{absEntry}): this job is the correctness safety net.
/// </summary>
[DisallowConcurrentExecution]
public sealed class PickListCacheFreshnessJob : IJob
{
    private readonly PickListMirrorFreshnessService      _freshness;
    private readonly ILogger<PickListCacheFreshnessJob>  _log;

    public PickListCacheFreshnessJob(
        PickListMirrorFreshnessService      freshness,
        ILogger<PickListCacheFreshnessJob>  log)
    {
        _freshness = freshness;
        _log       = log;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        _log.LogDebug("[PLCacheFreshness] Cycle started.");
        try
        {
            await _freshness.RefreshNonTerminalAsync(context.CancellationToken);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[PLCacheFreshness] Unhandled exception in freshness cycle.");
        }
        _log.LogDebug("[PLCacheFreshness] Cycle complete.");
    }
}
