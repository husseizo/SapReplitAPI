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
/// </summary>
[ApiController]
[Route("api/zf-admin")]
[ServiceFilter(typeof(ZfAdminKeyAuthFilter))]
[SupportedOSPlatform("windows")]
public sealed class ZfAdminController : ControllerBase
{
    private readonly ZfAdminDiagnosticService   _diagnostic;
    private readonly ZfAdminActionService       _actions;
    private readonly ZfAdminAuditRepository     _audit;
    private readonly ILogger<ZfAdminController> _log;

    public ZfAdminController(
        ZfAdminDiagnosticService    diagnostic,
        ZfAdminActionService        actions,
        ZfAdminAuditRepository      audit,
        ILogger<ZfAdminController>  log)
    {
        _diagnostic = diagnostic;
        _actions    = actions;
        _audit      = audit;
        _log        = log;
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
