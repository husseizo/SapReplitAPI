using Microsoft.Extensions.Options;
using Quartz;
using SapReplitAPI.Models.Offline;
using SapReplitAPI.Services.Offline;

namespace SapReplitAPI.Jobs;

/// <summary>
/// V2 recovery Quartz job. Completely separate from PendingOrderSyncJob.
///
/// ISOLATION: This job only processes OfflineFulfillmentV2 records.
///            PendingOrderSyncJob is untouched and continues to run independently.
///
/// CONCURRENCY: DisallowConcurrentExecution — only one recovery cycle runs at a time.
///              Cross-process claim locking inside RecoveryService handles worker races.
///
/// FEATURE GUARD: Job is a no-op when OfflineFulfillmentV2:Enabled = false.
/// </summary>
[DisallowConcurrentExecution]
public sealed class OfflineFulfillmentRecoveryJob : IJob
{
    private readonly OfflineFulfillmentRecoveryService _recovery;
    private readonly OfflineFulfillmentOptions _opts;
    private readonly ILogger<OfflineFulfillmentRecoveryJob> _log;

    public OfflineFulfillmentRecoveryJob(
        OfflineFulfillmentRecoveryService recovery,
        IOptions<OfflineFulfillmentOptions> opts,
        ILogger<OfflineFulfillmentRecoveryJob> log)
    {
        _recovery = recovery;
        _opts     = opts.Value;
        _log      = log;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        if (!_opts.Enabled)
        {
            _log.LogDebug("[OFFLINE-V2-JOB] Feature disabled — skipping recovery cycle.");
            return;
        }

        _log.LogDebug("[OFFLINE-V2-JOB] Recovery cycle started.");

        try
        {
            await _recovery.ReleaseStaleClaimsAsync(context.CancellationToken);
            await _recovery.RecoverBatchAsync(context.CancellationToken);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[OFFLINE-V2-JOB] Recovery cycle threw unhandled exception.");
        }

        _log.LogDebug("[OFFLINE-V2-JOB] Recovery cycle complete.");
    }
}
