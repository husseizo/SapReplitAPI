using Microsoft.Extensions.Options;
using Quartz;
using SapReplitAPI.Models.Returns;
using SapReplitAPI.Services.Returns;

namespace SapReplitAPI.Jobs;

/// <summary>
/// Incremental Return Request reconciliation — SAFETY NET only.
///
/// ISOLATION: Does not modify PendingReturnQuantityMirror, NeonReturnRequestWriteService,
///            ReturnRequestEventHandler, or the SAP outbox. The event-driven path remains the
///            primary synchronization mechanism and keeps running regardless of this job.
///
/// CONCURRENCY: DisallowConcurrentExecution — only one incremental run at a time. Each run uses
///              a sliding lookback window (now - LookbackMinutes), not a persisted checkpoint, so
///              a failed or skipped run cannot create a silent coverage gap: the next run's
///              window still covers it, as long as LookbackMinutes stays larger than the actual
///              gap between runs.
///
/// FEATURE GUARD: Job is a no-op when ReturnReconciliation:Enabled = false (default).
/// </summary>
[DisallowConcurrentExecution]
public sealed class ReturnRequestReconciliationJob : IJob
{
    private readonly ReturnRequestReconciliationService _service;
    private readonly ReturnReconciliationOptions _opts;
    private readonly ILogger<ReturnRequestReconciliationJob> _log;

    public ReturnRequestReconciliationJob(
        ReturnRequestReconciliationService service,
        IOptions<ReturnReconciliationOptions> opts,
        ILogger<ReturnRequestReconciliationJob> log)
    {
        _service = service;
        _opts = opts.Value;
        _log = log;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        if (!_opts.Enabled)
        {
            _log.LogDebug("[RETURN-RECON-JOB] Feature disabled — skipping incremental reconciliation.");
            return;
        }

        try
        {
            await _service.RunIncrementalAsync(_opts, context.CancellationToken);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[RETURN-RECON-JOB] Incremental reconciliation cycle threw unhandled exception.");
        }
    }
}
