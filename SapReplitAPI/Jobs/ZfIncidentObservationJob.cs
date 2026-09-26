using Quartz;
using SapReplitAPI.Services.ZoneFulfillment;

namespace SapReplitAPI.Jobs;

/// <summary>
/// Runs ZfIncidentObservationService.ObserveAsync on a fixed schedule so durable ZF
/// incident lifecycle history is kept current regardless of whether anyone has the
/// dashboard open. This is the ONLY thing that persists lifecycle state — dashboard
/// requests only ever read the already-persisted table (see ZfDashboardService).
///
/// Schedule: every 5 minutes (0 0/5 * * * ?). Pure SQL detection (no SAP DI API/COM
/// call), so no cross-job SAP concurrency concern — but [DisallowConcurrentExecution]
/// plus ZfIncidentObservationService's own static gate still prevent two observation
/// cycles (a Quartz tick and, if one is ever added, a manual trigger) from running
/// against the same rows at once.
///
/// Failure behavior: exceptions are logged and swallowed, never rethrown — a failed
/// scan must leave existing ACTIVE incidents exactly as they are (see
/// ZfIncidentObservationService's failure-safety contract) and must not fail the
/// Quartz trigger's misfire handling in a way that could mask the underlying issue
/// differently from every other read that already tolerates a down MolasIntegration
/// connection.
/// </summary>
[DisallowConcurrentExecution]
public class ZfIncidentObservationJob : IJob
{
    private readonly ZfIncidentObservationService _observation;
    private readonly ILogger<ZfIncidentObservationJob> _log;

    public ZfIncidentObservationJob(
        ZfIncidentObservationService observation,
        ILogger<ZfIncidentObservationJob> log)
    {
        _observation = observation;
        _log         = log;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        try
        {
            var result = await _observation.ObserveAsync(context.CancellationToken);
            if (!result.Success)
                _log.LogWarning("[ZF-OBSERVE-JOB] Cycle failed: {Error}", result.Error);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[ZF-OBSERVE-JOB] Unexpected exception — existing lifecycle rows left untouched.");
        }
    }
}
