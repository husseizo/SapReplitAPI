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
