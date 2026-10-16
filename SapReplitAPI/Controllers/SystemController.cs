using Microsoft.AspNetCore.Mvc;
using SapReplitAPI.Filters;
using SapReplitAPI.Services.Events;

namespace SapReplitAPI.Controllers;

[ApiController]
[Route("api/system")]
[ServiceFilter(typeof(ApiKeyAuthFilter))]
public sealed class SystemController : ControllerBase
{
    private readonly IServiceProvider _services;

    public SystemController(IServiceProvider services)
    {
        _services = services;
    }

    /// <summary>
    /// Returns the operational health of the SAP event pipeline.
    /// Requires API key. Does not expose credentials or connection strings.
    /// </summary>
    [HttpGet("event-pipeline-health")]
    public async Task<IActionResult> GetEventPipelineHealthAsync(CancellationToken ct)
    {
        // OutboxClaimService is only registered when MolasIntegration is configured.
        var claim = _services.GetService<OutboxClaimService>();
        bool configured = claim is not null;

        if (!configured)
        {
            return Ok(new EventPipelineHealthResponse
            {
                MolasIntegrationConfigured = false,
                DatabaseReachable          = false,
                OutboxPollerEnabled        = false,
                PendingEvents              = 0,
                ProcessingEvents           = 0,
                FailedEvents               = 0,
                OldestPendingAgeSeconds    = 0,
                LastProcessedEventUtc      = null,
                Status                     = "Degraded",
                StatusDetail               = "MolasIntegration not configured. Set env var ConnectionStrings__MolasIntegration."
            });
        }

        try
        {
            var h = await claim!.QueryHealthAsync(ct);

            string status;
            string detail;

            if (h.Failed > 0 && h.Pending > 0)
            {
                status = "Degraded";
                detail = $"{h.Failed} permanently Failed event(s); {h.Pending} Pending awaiting processing.";
            }
            else if (h.Failed > 0)
            {
                status = "Degraded";
                detail = $"{h.Failed} permanently Failed event(s) — inspect SapEventOutbox.LastError.";
            }
            else if (h.OldestPendingAgeSec > 300)
            {
                status = "Degraded";
                detail = $"Oldest Pending event is {h.OldestPendingAgeSec}s old — poller may be stuck.";
            }
            else if (h.Processing > 0 && h.OldestPendingAgeSec > 300)
            {
                status = "Degraded";
                detail = $"Event stuck in Processing for {h.OldestPendingAgeSec}s — lease recovery expected within 5 min.";
            }
            else
            {
                status = "Healthy";
                detail = h.Pending == 0
                    ? "No pending events. Pipeline idle and ready."
                    : $"{h.Pending} event(s) pending — will be claimed within 1s.";
            }

            return Ok(new EventPipelineHealthResponse
            {
                MolasIntegrationConfigured = true,
                DatabaseReachable          = true,
                OutboxPollerEnabled        = true,
                PendingEvents              = h.Pending,
                ProcessingEvents           = h.Processing,
                FailedEvents               = h.Failed,
                OldestPendingAgeSeconds    = (int)(h.OldestPendingAgeSec ?? 0),
                LastProcessedEventUtc      = h.LastProcessedUtc,
                Status                     = status,
                StatusDetail               = detail
            });
        }
        catch (Exception ex)
        {
            return Ok(new EventPipelineHealthResponse
            {
                MolasIntegrationConfigured = true,
                DatabaseReachable          = false,
                OutboxPollerEnabled        = true,
                PendingEvents              = 0,
                ProcessingEvents           = 0,
                FailedEvents               = 0,
                OldestPendingAgeSeconds    = 0,
                LastProcessedEventUtc      = null,
                Status                     = "Unhealthy",
                StatusDetail               = $"Database unreachable: {ex.GetType().Name}"
            });
        }
    }

    /// <summary>
    /// Returns the operational health of the Return Request reconciliation safety net
    /// (ReturnRequestReconciliationJob). Requires API key. Read-only; does not trigger a run.
    /// </summary>
    [HttpGet("return-reconciliation-health")]
    public async Task<IActionResult> GetReturnReconciliationHealthAsync(CancellationToken ct)
    {
        var opts = _services.GetService<Microsoft.Extensions.Options.IOptions<SapReplitAPI.Models.Returns.ReturnReconciliationOptions>>()?.Value;
        var service = _services.GetService<SapReplitAPI.Services.Returns.ReturnRequestReconciliationService>();

        if (opts is null || service is null)
        {
            return Ok(new ReturnReconciliationHealthResponse
            {
                Enabled = false,
                FullReconciliationEnabled = false,
                Status = "NotConfigured",
                StatusDetail = "ReturnReconciliation feature is not registered (requires Neon connection string)."
            });
        }

        try
        {
            var summary = await service.GetLastRunSummaryAsync(ct);
            var histSummary = await service.GetHistoricalRunSummaryAsync(ct);
            string status = !opts.Enabled ? "Disabled"
                : summary?.LastRunAtUtc is null ? "NeverRun"
                : (summary.LastRunErrors ?? 0) > 0 ? "Degraded"
                : "Healthy";

            return Ok(new ReturnReconciliationHealthResponse
            {
                Enabled = opts.Enabled,
                FullReconciliationEnabled = opts.FullReconciliationEnabled,
                HistoricalRotationEnabled = opts.HistoricalRotationEnabled,
                LastProcessedDocEntry = summary?.LastProcessedDocEntry,
                LastRunAtUtc = summary?.LastRunAtUtc,
                LastRunCandidatesExamined = summary?.LastRunCandidatesExamined,
                LastRunRefreshed = summary?.LastRunRefreshed,
                LastRunErrors = summary?.LastRunErrors,
                LastRunDurationMs = summary?.LastRunDurationMs,
                HistoricalCursorDocEntry = histSummary?.HistoricalCursorDocEntry,
                HistoricalLastRunAtUtc = histSummary?.HistoricalLastRunAtUtc,
                HistoricalLastRunRefreshed = histSummary?.HistoricalLastRunRefreshed,
                HistoricalLastRunErrors = histSummary?.HistoricalLastRunErrors,
                HistoricalLapsCompleted = histSummary?.LapsCompleted,
                Status = status,
                StatusDetail = !opts.Enabled
                    ? "Feature disabled (ReturnReconciliation:Enabled=false) — safety net is a no-op."
                    : summary?.LastRunAtUtc is null
                        ? "Enabled but has not run yet."
                        : $"Last run at {summary.LastRunAtUtc:u}: {summary.LastRunRefreshed} refreshed, {summary.LastRunErrors} error(s)."
            });
        }
        catch (Exception ex)
        {
            return Ok(new ReturnReconciliationHealthResponse
            {
                Enabled = opts.Enabled,
                FullReconciliationEnabled = opts.FullReconciliationEnabled,
                Status = "Unhealthy",
                StatusDetail = $"Failed to read reconciliation state: {ex.GetType().Name}"
            });
        }
    }
}

public sealed class ReturnReconciliationHealthResponse
{
    public bool      Enabled                      { get; set; }
    public bool      FullReconciliationEnabled     { get; set; }
    public bool      HistoricalRotationEnabled     { get; set; }
    public int?      LastProcessedDocEntry         { get; set; }
    public DateTime? LastRunAtUtc                  { get; set; }
    public int?      LastRunCandidatesExamined     { get; set; }
    public int?      LastRunRefreshed              { get; set; }
    public int?      LastRunErrors                 { get; set; }
    public long?     LastRunDurationMs             { get; set; }
    public int?      HistoricalCursorDocEntry      { get; set; }
    public DateTime? HistoricalLastRunAtUtc        { get; set; }
    public int?      HistoricalLastRunRefreshed    { get; set; }
    public int?      HistoricalLastRunErrors       { get; set; }
    public int?      HistoricalLapsCompleted       { get; set; }
    public string    Status                        { get; set; } = "Unknown";
    public string    StatusDetail                  { get; set; } = "";
}

public sealed class EventPipelineHealthResponse
{
    public bool       MolasIntegrationConfigured { get; set; }
    public bool       DatabaseReachable          { get; set; }
    public bool       OutboxPollerEnabled        { get; set; }
    public int        PendingEvents              { get; set; }
    public int        ProcessingEvents           { get; set; }
    public int        FailedEvents               { get; set; }
    public int        OldestPendingAgeSeconds    { get; set; }
    public DateTime?  LastProcessedEventUtc      { get; set; }
    public string     Status                     { get; set; } = "Unknown";
    public string     StatusDetail               { get; set; } = "";
}
