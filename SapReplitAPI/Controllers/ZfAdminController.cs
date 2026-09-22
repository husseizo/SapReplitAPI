using Microsoft.AspNetCore.Mvc;
using SapReplitAPI.Filters;
using SapReplitAPI.Models.ZoneFulfillment;
using SapReplitAPI.Services.ZoneFulfillment;
using System.Runtime.Versioning;

namespace SapReplitAPI.Controllers;

/// <summary>
/// ZF Operations Console.
///
/// All routes require X-ZF-Admin-Key header (ZfAdminKeyAuthFilter).
///
/// Phase 1A — read-only:
///   GET  /api/zf-admin/orders/{soDocEntry}
///   GET  /api/zf-admin/orders/{soDocEntry}/timeline
///   GET  /api/zf-admin/orders/{soDocEntry}/incidents
///   GET  /api/zf-admin/orders/{soDocEntry}/audit
///
/// Phase 1B — controlled recovery (each POST pre-flights live state):
///   POST /api/zf-admin/orders/{soDocEntry}/refresh
///   POST /api/zf-admin/orders/{soDocEntry}/replan-released
///   POST /api/zf-admin/orders/{soDocEntry}/resume-replan
///   POST /api/zf-admin/orders/{soDocEntry}/retry-delivery
///   POST /api/zf-admin/orders/{soDocEntry}/retry-invoice
///
/// Phase 4 — Diagnosis Console (read-only + resolution tracking):
///   GET  /api/zf-admin/orders/{docNum}/diagnostic-incidents
///   POST /api/zf-admin/orders/{docNum}/diagnostic-incidents/{incidentKey}/resolve
///   GET  /api/zf-admin/orders/{docNum}/diagnostic-incidents/{incidentKey}/resolutions
/// </summary>
[ApiController]
[Route("api/zf-admin")]
[ServiceFilter(typeof(ZfAdminKeyAuthFilter))]
[SupportedOSPlatform("windows")]
public sealed class ZfAdminController : ControllerBase
{
    private readonly ZfAdminDiagnosticService          _diagnostic;
    private readonly ZfAdminActionService              _actions;
    private readonly ZfAdminAuditRepository            _audit;
    private readonly ZfIncidentResolutionRepository    _incidentRepo;
    private readonly ILogger<ZfAdminController>        _log;

    public ZfAdminController(
        ZfAdminDiagnosticService         diagnostic,
        ZfAdminActionService             actions,
        ZfAdminAuditRepository           audit,
        ZfIncidentResolutionRepository   incidentRepo,
        ILogger<ZfAdminController>       log)
    {
        _diagnostic   = diagnostic;
        _actions      = actions;
        _audit        = audit;
        _incidentRepo = incidentRepo;
        _log          = log;
    }

    // ── Phase 1A: read-only ───────────────────────────────────────────────────

    [HttpGet("orders/{soDocEntry:int}")]
    public async Task<IActionResult> GetOrderDiagnostic(
        int soDocEntry, CancellationToken ct)
    {
        _log.LogInformation("[ZfAdmin] GET diagnostic SoDocEntry={So}", soDocEntry);
        var result = await _diagnostic.GetOrderDiagnosticAsync(soDocEntry, ct);
        if (result is null)
            return NotFound(new { message = $"No ZF orchestration found for SoDocEntry={soDocEntry}." });
        return Ok(result);
    }

    [HttpGet("orders/{soDocEntry:int}/timeline")]
    public async Task<IActionResult> GetOrderTimeline(
        int soDocEntry, CancellationToken ct)
    {
        _log.LogInformation("[ZfAdmin] GET timeline SoDocEntry={So}", soDocEntry);
        var result = await _diagnostic.GetOrderTimelineAsync(soDocEntry, ct);
        if (result is null)
            return NotFound(new { message = $"No ZF orchestration found for SoDocEntry={soDocEntry}." });
        return Ok(result);
    }

    [HttpGet("orders/{soDocEntry:int}/incidents")]
    public async Task<IActionResult> GetOrderIncidents(
        int soDocEntry, CancellationToken ct)
    {
        _log.LogInformation("[ZfAdmin] GET incidents SoDocEntry={So}", soDocEntry);
        var result = await _diagnostic.GetOrderIncidentsAsync(soDocEntry, ct);
        if (result is null)
            return NotFound(new { message = $"No ZF orchestration found for SoDocEntry={soDocEntry}." });
        return Ok(result);
    }

    [HttpGet("orders/{soDocEntry:int}/audit")]
    public async Task<IActionResult> GetOrderAudit(
        int soDocEntry, [FromQuery] int limit = 50, CancellationToken ct = default)
    {
        _log.LogInformation("[ZfAdmin] GET audit SoDocEntry={So}", soDocEntry);
        var entries = await _audit.GetByOrderAsync(soDocEntry, limit, ct);
        return Ok(entries);
    }

    // ── Phase 1B: controlled recovery ────────────────────────────────────────

    /// <summary>
    /// Re-reads live diagnostic. No mutation, no audit record.
    /// 200 = refreshed state. 404 = no orchestration found.
    /// </summary>
    [HttpPost("orders/{soDocEntry:int}/refresh")]
    public async Task<IActionResult> RefreshState(
        int soDocEntry, [FromBody] ZfAdminActionRequest req, CancellationToken ct)
    {
        _log.LogInformation("[ZfAdmin] POST refresh SoDocEntry={So} By={By}", soDocEntry, req.RequestedBy);
        var result = await _actions.RefreshStateAsync(soDocEntry, req.RequestedBy, ct);
        return ActionResult(result);
    }

    /// <summary>
    /// REPLAN_RELEASED: reconcile AMBER stale-fragment on pre-pick order.
    /// Preconditions: zero physical picks, no active delivery.
    /// 200 = success. 409 = state changed or precondition failed.
    /// </summary>
    [HttpPost("orders/{soDocEntry:int}/replan-released")]
    public async Task<IActionResult> ReplanReleased(
        int soDocEntry, [FromBody] ZfAdminActionRequest req, CancellationToken ct)
    {
        _log.LogInformation("[ZfAdmin] POST replan-released SoDocEntry={So} By={By}", soDocEntry, req.RequestedBy);
        var result = await _actions.ReplanReleasedAsync(
            soDocEntry, req.RequestedBy, req.ExpectedOrchestrationState, ct);
        return ActionResult(result);
    }

    /// <summary>
    /// RESUME_REPLAN: resume an AMBER replan stuck in RecoveryRequired.
    /// 200 = success. 409 = state changed or precondition failed.
    /// </summary>
    [HttpPost("orders/{soDocEntry:int}/resume-replan")]
    public async Task<IActionResult> ResumeReplan(
        int soDocEntry, [FromBody] ZfAdminResumeReplanRequest req, CancellationToken ct)
    {
        _log.LogInformation("[ZfAdmin] POST resume-replan SoDocEntry={So} OpId={Op} By={By}",
            soDocEntry, req.OperationId, req.RequestedBy);
        var result = await _actions.ResumeReplanAsync(
            soDocEntry, req.OperationId, req.RequestedBy, req.ExpectedOrchestrationState, ct);
        return ActionResult(result);
    }

    /// <summary>
    /// RETRY_DELIVERY: create ODLN via existing delivery gate. Never bypasses preflight.
    /// 200 = success. 409 = state changed, precondition, or gate blocked.
    /// </summary>
    [HttpPost("orders/{soDocEntry:int}/retry-delivery")]
    public async Task<IActionResult> RetryDelivery(
        int soDocEntry, [FromBody] ZfAdminRetryDeliveryRequest req, CancellationToken ct)
    {
        _log.LogInformation("[ZfAdmin] POST retry-delivery SoDocEntry={So} By={By}", soDocEntry, req.RequestedBy);
        var result = await _actions.RetryDeliveryAsync(
            soDocEntry, req.RequestedBy, req.ExpectedOrchestrationState, ct);
        return ActionResult(result);
    }

    /// <summary>
    /// RETRY_INVOICE: create OINV for an existing delivery that has no invoice.
    /// 200 = success. 409 = state changed, precondition, or delivery absent.
    /// </summary>
    [HttpPost("orders/{soDocEntry:int}/retry-invoice")]
    public async Task<IActionResult> RetryInvoice(
        int soDocEntry, [FromBody] ZfAdminRetryInvoiceRequest req, CancellationToken ct)
    {
        _log.LogInformation("[ZfAdmin] POST retry-invoice SoDocEntry={So} Delivery={D} By={By}",
            soDocEntry, req.DeliveryDocEntry, req.RequestedBy);
        var result = await _actions.RetryInvoiceAsync(
            soDocEntry, req.DeliveryDocEntry, req.RequestedBy, req.ExpectedOrchestrationState, ct);
        return ActionResult(result);
    }

    /// <summary>
    /// RECONCILE_STALE_FRAGMENT_AFTER_VALID_PICK: update fragment WhsCode to match
    /// actual pick warehouse when fragment metadata is the sole inconsistency.
    /// Requires all 15 live preconditions to pass. Optimistic-concurrency safe.
    /// 200 = success. 409 = state changed, precondition failed, or concurrency conflict.
    /// </summary>
    [HttpPost("orders/{soDocEntry:int}/reconcile-fragment")]
    public async Task<IActionResult> ReconcileFragment(
        int soDocEntry, [FromBody] ZfAdminReconcileFragmentRequest req, CancellationToken ct)
    {
        _log.LogInformation("[ZfAdmin] POST reconcile-fragment SoDocEntry={So} By={By}",
            soDocEntry, req.RequestedBy);
        var result = await _actions.ReconcileStaleFragmentAsync(
            soDocEntry, req.RequestedBy, req.ExpectedOrchestrationState, ct);
        return ActionResult(result);
    }

    // ── Phase 4: Diagnosis Console (B2/B5/B6) ────────────────────────────────

    /// <summary>
    /// Returns Phase 3 diagnostic incidents for the given SO DocNum (user-visible number).
    /// This is separate from the legacy /incidents route (which uses the old ZfOrderIncidentResult model).
    /// 200 = incidents result (may be empty). 404 = no orchestration for this DocNum.
    /// </summary>
    [HttpGet("orders/{docNum:int}/diagnostic-incidents")]
    public async Task<IActionResult> GetDiagnosticIncidents(
        int docNum, CancellationToken ct)
    {
        _log.LogInformation("[ZfAdmin] GET diagnostic-incidents DocNum={DocNum}", docNum);
        var result = await _diagnostic.GetOrderDiagnosticIncidentsByDocNumAsync(docNum, ct);
        if (result is null)
            return NotFound(new { message = $"No ZF orchestration found for DocNum={docNum}." });
        return Ok(result);
    }

    /// <summary>
    /// Records a manual resolution for a specific diagnostic incident.
    /// This is tracking-only — no SAP mutations are performed.
    /// MutationAvailable is always false for all resolutions.
    ///
    /// incidentKey format: "{docNum}_{fragmentId}_{incidentCode}"
    /// e.g. "28879_20092_ZF_FRAGMENT_RDR1_MISSING"
    ///
    /// Allowed resolutions (ZfIncidentResolution constants):
    ///   ACKNOWLEDGED_EXTERNAL_SAP_EDIT → Status=RESOLVED
    ///   RESTORE_REQUIRED               → Status=ACKNOWLEDGED
    ///   CANCEL_FRAGMENT_REQUIRED       → Status=ACKNOWLEDGED
    ///   FALSE_POSITIVE                 → Status=RESOLVED
    ///   DEFERRED                       → Status=DEFERRED
    ///
    /// 201 = resolution recorded. 400 = invalid input.
    /// 404 = no orchestration for this DocNum.
    /// </summary>
    [HttpPost("orders/{docNum:int}/diagnostic-incidents/{incidentKey}/resolve")]
    public async Task<IActionResult> ResolveIncident(
        int docNum, string incidentKey,
        [FromBody] ZfIncidentResolutionRequest req,
        CancellationToken ct)
    {
        _log.LogInformation("[ZfAdmin] POST resolve DocNum={DocNum} Key={Key} Resolution={Res} By={Op}",
            docNum, incidentKey, req.Resolution, req.Operator);

        // B9 Safety: no SAP mutations are ever called from this endpoint.

        // Validate resolution value
        if (!ZfIncidentResolution.AllValues.Contains(req.Resolution))
            return BadRequest(new
            {
                error    = $"Invalid resolution '{req.Resolution}'.",
                allowed  = ZfIncidentResolution.AllValues
            });

        // Require operator and reason
        if (string.IsNullOrWhiteSpace(req.Operator))
            return BadRequest(new { error = "Operator is required." });
        if (string.IsNullOrWhiteSpace(req.Reason))
            return BadRequest(new { error = "Reason is required." });

        // Verify the orchestration exists for this DocNum
        var incidentsResult = await _diagnostic.GetOrderDiagnosticIncidentsByDocNumAsync(docNum, ct);
        if (incidentsResult is null)
            return NotFound(new { message = $"No ZF orchestration found for DocNum={docNum}." });

        // Capture incident evidence at resolution time for history preservation (B7/B8)
        var matchedIncident = incidentsResult.Incidents
            .FirstOrDefault(i => i.FragmentId.HasValue &&
                ZfIncidentResolutionRepository.BuildIncidentKey(
                    docNum, i.FragmentId, i.Code) == incidentKey);

        string? evidenceJson = matchedIncident is not null
            ? ZfIncidentResolutionRepository.SerializeEvidence(matchedIncident.Evidence)
            : null;

        // Parse fragmentId from incidentKey (format: "{docNum}_{fragmentId}_{code}")
        long? fragmentId = null;
        var parts = incidentKey.Split('_', 3);
        if (parts.Length >= 2 && long.TryParse(parts[1], out var fid))
            fragmentId = fid;

        // Determine incident code from the key (everything after first two segments)
        string incidentCode = parts.Length >= 3 ? parts[2] : incidentKey;

        // Map resolution → status
        string resultingStatus = ZfIncidentResolution.ToIncidentStatus(req.Resolution);

        var record = new ZfIncidentResolutionRecord
        {
            IncidentKey     = incidentKey,
            SoDocNum        = docNum,
            SoDocEntry      = incidentsResult.SoDocEntry,
            OrchestrationId = matchedIncident?.OrchestrationId,
            FragmentId      = fragmentId,
            IncidentCode    = incidentCode,
            Resolution      = req.Resolution,
            Status          = resultingStatus,
            Operator        = req.Operator,
            Reason          = req.Reason,
            ResolvedAtUtc   = DateTime.UtcNow,
            EvidenceJson    = evidenceJson,
        };

        var newId = await _incidentRepo.InsertResolutionAsync(record, ct);
        record.Id = newId;

        return StatusCode(201, new
        {
            message          = $"Resolution '{req.Resolution}' recorded. Incident status → {resultingStatus}.",
            incidentKey      = incidentKey,
            resultingStatus  = resultingStatus,
            mutationAvailable = false,   // B9: always false — this endpoint never mutates SAP
            resolution       = record,
        });
    }

    /// <summary>
    /// Returns the full append-only resolution history for the given incident key.
    /// 200 = history list (may be empty). 404 = no orchestration for this DocNum.
    /// </summary>
    [HttpGet("orders/{docNum:int}/diagnostic-incidents/{incidentKey}/resolutions")]
    public async Task<IActionResult> GetResolutionHistory(
        int docNum, string incidentKey, CancellationToken ct)
    {
        _log.LogInformation("[ZfAdmin] GET resolutions DocNum={DocNum} Key={Key}", docNum, incidentKey);

        // Verify orchestration exists
        var incidentsResult = await _diagnostic.GetOrderDiagnosticIncidentsByDocNumAsync(docNum, ct);
        if (incidentsResult is null)
            return NotFound(new { message = $"No ZF orchestration found for DocNum={docNum}." });

        var history = await _incidentRepo.GetResolutionsAsync(incidentKey, ct);
        return Ok(new
        {
            incidentKey    = incidentKey,
            docNum         = docNum,
            totalRecords   = history.Count,
            currentStatus  = history.Count > 0
                ? history[^1].Status
                : ZfIncidentStatusValue.Active,
            history        = history,
        });
    }

    // ── HTTP status mapping ───────────────────────────────────────────────────

    private IActionResult ActionResult(ZfAdminActionResult result)
    {
        if (result.IsSuccess) return Ok(result);

        return result.ErrorCode switch
        {
            ZfAdminActionError.OrchestrationNotFound
                => NotFound(new { result.ErrorCode, result.ErrorMessage }),
            ZfAdminActionError.StateChanged
            or ZfAdminActionError.PreconditionFailed
            or ZfAdminActionError.ActionNotAvailable
                => Conflict(new { result.ErrorCode, result.ErrorMessage, before = result.BeforeState?.Consistency }),
            _   => StatusCode(500, new { result.ErrorCode, result.ErrorMessage }),
        };
    }
}
