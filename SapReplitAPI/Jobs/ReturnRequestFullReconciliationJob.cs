using Microsoft.Extensions.Options;
using Quartz;
using SapReplitAPI.Models.Returns;
using SapReplitAPI.Services.Returns;

namespace SapReplitAPI.Jobs;

/// <summary>
/// Full Return Request / Credit Memo reconciliation — the "eventual full reconciliation"
/// correctness guarantee required alongside the incremental, lookback-window job
/// (ReturnRequestReconciliationJob), because a timestamp-based incremental process cannot
/// prove it has caught every possible missed event outside its own window.
///
/// This job does not implement its own recompute logic — it is a thin, scheduled wrapper around
/// the existing, unmodified ReturnsMirrorBackfillService.RunAsync(), the same manual backfill
/// capability already reachable via the `--backfill-returns-mirror` CLI flag and the existing
/// admin HTTP endpoint. Both of those manual paths remain available and unchanged.
///
/// COST: ReturnsMirrorBackfillService re-enumerates every ORRR/ORIN document ever created (one
/// SAP DI API round-trip each) and ends with a Postgres SHARE ROW EXCLUSIVE table lock across
/// CreditMemoHeaders/CreditMemoLines/ReturnRequests/ReturnRequestLines/InvoiceLines while it
/// recomputes every InvoiceLines row. This is why it is scheduled rarely (default: nightly) and
/// why ReturnRequestReconciliationJob exists as the frequent, lightweight path instead of simply
/// running this job every hour.
///
/// CONCURRENCY: DisallowConcurrentExecution. The table lock above will block the event-driven
/// path's writes to the same tables for the duration of this job — this is a pre-existing
/// characteristic of ReturnsMirrorBackfillService that this task does not modify (see the P1
/// security/reconciliation delivery report's "concurrency and data integrity" section for the
/// documented gap and why a code-level fix is out of scope here).
///
/// FEATURE GUARD: Job is a no-op when ReturnReconciliation:FullReconciliationEnabled = false
/// (default) — a separate flag from the incremental job's Enabled, so each can be turned on
/// independently.
/// </summary>
[DisallowConcurrentExecution]
public sealed class ReturnRequestFullReconciliationJob : IJob
{
    private readonly ReturnsMirrorBackfillService _backfill;
    private readonly ReturnReconciliationOptions _opts;
    private readonly ILogger<ReturnRequestFullReconciliationJob> _log;

    public ReturnRequestFullReconciliationJob(
        ReturnsMirrorBackfillService backfill,
        IOptions<ReturnReconciliationOptions> opts,
        ILogger<ReturnRequestFullReconciliationJob> log)
    {
        _backfill = backfill;
        _opts = opts.Value;
        _log = log;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        if (!_opts.FullReconciliationEnabled)
        {
            _log.LogDebug("[RETURN-FULL-RECON-JOB] Feature disabled — skipping full reconciliation.");
            return;
        }

        var startedAtUtc = DateTime.UtcNow;
        _log.LogInformation("[RETURN-FULL-RECON-JOB] Full reconciliation started at {StartedAtUtc}.", startedAtUtc);

        try
        {
            var report = await _backfill.RunAsync(context.CancellationToken);
            _log.LogInformation(
                "[RETURN-FULL-RECON-JOB] Full reconciliation complete. OrrrHeaders={OrrrHeaders} OrrrLines={OrrrLines} OrinHeaders={OrinHeaders} OrinLines={OrinLines} AffectedInvoiceLines={AffectedInvoiceLines} Errors={ErrorCount} DurationMs={DurationMs}",
                report.OrrrHeaders, report.OrrrLines, report.OrinHeaders, report.OrinLines,
                report.AffectedInvoiceLines, report.Errors.Count,
                (DateTime.UtcNow - startedAtUtc).TotalMilliseconds);

            if (report.Errors.Count > 0)
                _log.LogWarning("[RETURN-FULL-RECON-JOB] Errors: {Errors}", string.Join(" | ", report.Errors));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[RETURN-FULL-RECON-JOB] Full reconciliation threw unhandled exception.");
        }
    }
}
