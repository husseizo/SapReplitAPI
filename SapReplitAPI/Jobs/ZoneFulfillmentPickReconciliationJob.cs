using Quartz;
using SapReplitAPI.Services.ZoneFulfillment;

namespace SapReplitAPI.Jobs;

/// <summary>
/// Quartz job — fires every 30 seconds, detects SAP-native OPKL confirmations,
/// reconciles MolasIntegration PLR state, and delegates to EvaluateAndTriggerDeliveryAsync.
///
/// No-op when no Accepted orchestrations have stuck non-Picked PLRs older than 90 seconds.
/// </summary>
[DisallowConcurrentExecution]
public sealed class ZoneFulfillmentPickReconciliationJob : IJob
{
    private readonly ZoneFulfillmentPickReconciliationService      _reconciliation;
    private readonly ILogger<ZoneFulfillmentPickReconciliationJob> _log;

    public ZoneFulfillmentPickReconciliationJob(
        ZoneFulfillmentPickReconciliationService      reconciliation,
        ILogger<ZoneFulfillmentPickReconciliationJob> log)
    {
        _reconciliation = reconciliation;
        _log            = log;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        _log.LogDebug("[ZF-PICK-RECONCILE-JOB] Cycle started.");
        try
        {
            await _reconciliation.ReconcileBatchAsync(context.CancellationToken);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[ZF-PICK-RECONCILE-JOB] Unhandled exception in reconciliation cycle.");
        }
        _log.LogDebug("[ZF-PICK-RECONCILE-JOB] Cycle complete.");
    }
}
