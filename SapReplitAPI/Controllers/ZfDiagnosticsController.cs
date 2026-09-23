using Microsoft.AspNetCore.Mvc;
using SapReplitAPI.Filters;
using SapReplitAPI.Models.ZoneFulfillment;
using SapReplitAPI.Services.ZoneFulfillment;
using System.Runtime.Versioning;

namespace SapReplitAPI.Controllers;

/// <summary>
/// ZF Diagnosis Console — Phase 2 dashboard endpoints.
///
/// All routes require X-ZF-Admin-Key header (ZfAdminKeyAuthFilter).
///
/// Phase 2:
///   GET /api/zf-admin/diagnostics/summary   — aggregated counts
///   GET /api/zf-admin/diagnostics/incidents — paged incident list with filters
///
/// Safety: read-only, no SAP mutations of any kind.
/// TechnicalStatus and ResolutionStatus are always kept as separate dimensions.
/// </summary>
[ApiController]
[Route("api/zf-admin/diagnostics")]
[ServiceFilter(typeof(ZfAdminKeyAuthFilter))]
[SupportedOSPlatform("windows")]
public sealed class ZfDiagnosticsController : ControllerBase
{
    private readonly ZfDashboardService              _dashboard;
    private readonly ILogger<ZfDiagnosticsController> _log;

    public ZfDiagnosticsController(
        ZfDashboardService               dashboard,
        ILogger<ZfDiagnosticsController> log)
    {
        _dashboard = dashboard;
        _log       = log;
    }

    /// <summary>
    /// Returns aggregated diagnostic counts.
    /// Keeps TechnicalStatus (live SAP detection) separate from ResolutionStatus (human decisions).
    /// 200 = summary. 500 = query failure (logged, non-fatal for other endpoints).
    /// </summary>
    [HttpGet("summary")]
    public async Task<IActionResult> GetSummary(CancellationToken ct)
    {
        _log.LogInformation("[ZfDiagnostics] GET summary");
        try
        {
            var summary = await _dashboard.GetSummaryAsync(ct);
            return Ok(summary);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[ZfDiagnostics] GetSummary failed");
            return StatusCode(500, new { error = "Dashboard summary query failed.", detail = ex.Message });
        }
    }

    /// <summary>
    /// Returns a paged, filtered list of ZF diagnostic incidents.
    /// Supports server-side filtering by TechnicalStatus, ResolutionStatus, severity, code,
    /// category, SO number, ItemCode, fragment/orchestration IDs, date range, aging bucket.
    ///
    /// 200 = paged list. 400 = invalid query params. 500 = query failure.
    /// </summary>
    [HttpGet("incidents")]
    public async Task<IActionResult> GetIncidents(
        [FromQuery] string?   technicalStatus  = null,
        [FromQuery] string?   resolutionStatus = null,
        [FromQuery] string?   severity         = null,
        [FromQuery] string?   incidentCode     = null,
        [FromQuery] string?   category         = null,
        [FromQuery] int?      soDocNum         = null,
        [FromQuery] string?   itemCode         = null,
        [FromQuery] long?     fragmentId       = null,
        [FromQuery] long?     orchestrationId  = null,
        [FromQuery] DateTime? dateFrom         = null,
        [FromQuery] DateTime? dateTo           = null,
        [FromQuery] bool?     hasResolution    = null,
        [FromQuery] string?   agingBucket      = null,
        [FromQuery] int       page             = 1,
        [FromQuery] int       pageSize         = 50,
        [FromQuery] string    sort             = "detectedAt",
        [FromQuery] string    sortDir          = "desc",
        CancellationToken ct = default)
    {
        _log.LogInformation(
            "[ZfDiagnostics] GET incidents tech={Tech} res={Res} sev={Sev} so={So} page={P}",
            technicalStatus, resolutionStatus, severity, soDocNum, page);

        if (page < 1 || pageSize < 1 || pageSize > 200)
            return BadRequest(new { error = "page must be ≥1; pageSize must be 1–200." });

        var query = new ZfIncidentListQuery
        {
            TechnicalStatus  = technicalStatus,
            ResolutionStatus = resolutionStatus,
            Severity         = severity,
            IncidentCode     = incidentCode,
            Category         = category,
            SoDocNum         = soDocNum,
            ItemCode         = itemCode,
            FragmentId       = fragmentId,
            OrchestrationId  = orchestrationId,
            DateFrom         = dateFrom,
            DateTo           = dateTo,
            HasResolution    = hasResolution,
            AgingBucket      = agingBucket,
            Page             = page,
            PageSize         = pageSize,
            Sort             = sort,
            SortDir          = sortDir,
        };

        try
        {
            var result = await _dashboard.GetIncidentListAsync(query, ct);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[ZfDiagnostics] GetIncidents failed");
            return StatusCode(500, new { error = "Incident list query failed.", detail = ex.Message });
        }
    }
}
