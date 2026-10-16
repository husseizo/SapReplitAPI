using Microsoft.Extensions.Options;
using Quartz;
using SapReplitAPI.Models.Returns;
using SapReplitAPI.Services.Returns;

namespace SapReplitAPI.Jobs;

/// <summary>
/// Historical rotation safety net (Phase 4 TASK 2) — eventually revisits EVERY ORRR DocEntry
/// ever created, not just recently-changed ones, closing the gap the incremental (timestamp-
/// driven) job cannot prove it covers: a delayed credit-memo cancellation that never reliably
/// bumps any SAP timestamp.
///
/// Unlike the full reconciliation job, this NEVER takes a table lock and NEVER re-enumerates the
/// whole dataset in a single pass — it processes HistoricalBatchSize documents per run via the
/// same lightweight per-document refresh path, rotating through a persisted cursor
/// (SelectBatch — the same starvation-proof algorithm the incremental job uses) so the full set
/// is revisited within a bounded number of runs, then the rotation starts over.
///
/// CONCURRENCY: DisallowConcurrentExecution prevents this job from overlapping itself. A
/// Postgres advisory lock inside ReturnRequestReconciliationService.RefreshOneAsync prevents it
/// from overlapping ReturnRequestReconciliationJob on the same DocEntry.
///
/// FEATURE GUARD: no-op when ReturnReconciliation:HistoricalRotationEnabled = false (default).
/// </summary>
[DisallowConcurrentExecution]
public sealed class ReturnRequestHistoricalRotationJob : IJob
{
    private readonly ReturnRequestReconciliationService _service;
    private readonly ReturnReconciliationOptions _opts;
    private readonly ILogger<ReturnRequestHistoricalRotationJob> _log;

    public ReturnRequestHistoricalRotationJob(
        ReturnRequestReconciliationService service,
        IOptions<ReturnReconciliationOptions> opts,
        ILogger<ReturnRequestHistoricalRotationJob> log)
    {
        _service = service;
        _opts = opts.Value;
        _log = log;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        if (!_opts.HistoricalRotationEnabled)
        {
            _log.LogDebug("[RETURN-HIST-RECON-JOB] Feature disabled — skipping historical rotation cycle.");
            return;
        }

        try
        {
            await _service.RunHistoricalRotationAsync(_opts, context.CancellationToken);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[RETURN-HIST-RECON-JOB] Historical rotation cycle threw unhandled exception.");
        }
    }
}
