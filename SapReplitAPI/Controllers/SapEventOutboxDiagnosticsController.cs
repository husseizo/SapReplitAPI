using Microsoft.AspNetCore.Mvc;
using SapReplitAPI.Filters;
using SapReplitAPI.Services.Events;
using System.Linq;

namespace SapReplitAPI.Controllers;

/// <summary>
/// SapEventOutbox observability — Phase 1 (Real-Time Neon Foundation) instrumentation.
///
/// All routes require X-ZF-Admin-Key (ZfAdminKeyAuthFilter — the same shared internal
/// admin key already used by the ZF diagnostics surface; this is a read-only internal
/// operations endpoint, not a new auth scheme).
///
/// Safety: read-only. No SAP mutations, no outbox mutations, no replay.
/// </summary>
[ApiController]
[Route("api/admin/outbox")]
[ServiceFilter(typeof(ZfAdminKeyAuthFilter))]
public sealed class SapEventOutboxDiagnosticsController : ControllerBase
{
    private readonly OutboxClaimService _claim;
    private readonly ILogger<SapEventOutboxDiagnosticsController> _log;

    public SapEventOutboxDiagnosticsController(
        OutboxClaimService claim,
        ILogger<SapEventOutboxDiagnosticsController> log)
    {
        _claim = claim;
        _log   = log;
    }

    /// <summary>
    /// GET /api/admin/outbox/health
    /// Pending/Processing/Failed/Done counts, oldest pending age, throughput/minute,
    /// ClaimLag + processing-duration percentiles (from rows with both ClaimedAtUtc
    /// and ProcessedAtUtc populated — prospective only, per the instrumentation fix),
    /// and a per-ObjectType/TransactionType/Priority breakdown.
    ///
    /// ?hours= windows the percentile/breakdown query (default 24h) — keeps the query
    /// cheap regardless of total retained SapEventOutbox history.
    /// </summary>
    [HttpGet("health")]
    public async Task<IActionResult> GetHealth([FromQuery] int hours, CancellationToken ct)
    {
        int window = hours > 0 ? hours : 24;
        _log.LogInformation("[OutboxDiagnostics] GET health windowHours={Hours}", window);
        try
        {
            var detail = await _claim.QueryDetailedHealthAsync(window, ct);
            return Ok(new
            {
                pending          = detail.Counts.Pending,
                processing       = detail.Counts.Processing,
                failed           = detail.Counts.Failed,
                done             = detail.Counts.Done,
                oldestPendingAgeSec = detail.Counts.OldestPendingAgeSec,
                lastProcessedUtc = detail.Counts.LastProcessedUtc,
                doneLastMinute   = detail.DoneLastMinute,
                windowHours      = detail.WindowHours,
                sampleSize       = detail.SampleSize,
                claimLagMs       = new { p50 = detail.ClaimLagP50Ms, p95 = detail.ClaimLagP95Ms, p99 = detail.ClaimLagP99Ms },
                processingMs     = new { p50 = detail.ProcessingP50Ms, p95 = detail.ProcessingP95Ms, p99 = detail.ProcessingP99Ms },
                breakdown        = detail.Breakdown.Select(b => new
                {
                    objectType      = b.ObjectType,
                    transactionType = b.TransactionType,
                    priority        = b.Priority,
                    count           = b.Count
                })
            });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[OutboxDiagnostics] GetHealth failed");
            return StatusCode(500, new { error = "Outbox health query failed.", detail = ex.Message });
        }
    }
}
