using Microsoft.AspNetCore.Mvc;
using SapReplitAPI.DTOs.ZoneFulfillment;
using SapReplitAPI.Models.ZoneFulfillment;
using SapReplitAPI.Services.ZoneFulfillment;
using System.Runtime.Versioning;

namespace SapReplitAPI.Controllers;

/// <summary>
/// C14: POST /api/zone-fulfillment/experimental/orders
/// C15: GET  /api/zone-fulfillment/experimental/orders/{requestId}/status
/// C16: POST /api/zone-fulfillment/experimental/plan  (dry-run, non-reserving)
///
/// Route prefix includes /experimental/ — explicit isolation marker.
/// All endpoints require X-Zone-Experimental: true header (authentication guard).
/// Production POST /api/orders is NOT affected.
/// </summary>
[ApiController]
[Route("api/zone-fulfillment/experimental")]
[SupportedOSPlatform("windows")]
public sealed class ZoneFulfillmentController : ControllerBase
{
    private readonly ZoneFulfillmentOrchestrationService _orch;
    private readonly ZoneFulfillmentRepository           _repo;
    private readonly ZoneAllocationEngine                _allocator;
    private readonly SapOitwAdapter                      _oitw;
    private readonly ILogger<ZoneFulfillmentController>  _log;

    public ZoneFulfillmentController(
        ZoneFulfillmentOrchestrationService orch,
        ZoneFulfillmentRepository           repo,
        ZoneAllocationEngine                allocator,
        SapOitwAdapter                      oitw,
        ILogger<ZoneFulfillmentController>  log)
    {
        _orch      = orch;
        _repo      = repo;
        _allocator = allocator;
        _oitw      = oitw;
        _log       = log;
    }

    // ── C14: Create order ──────────────────────────────────────────────────────

    /// <summary>
    /// POST /api/zone-fulfillment/experimental/orders
    /// 201 Created  — new SO accepted
    /// 200 OK       — idempotent (same RequestId + same payload, SO already exists)
    /// 202 Accepted — UnknownOutcome (SAP state uncertain, poll status)
    /// 400 Bad Request — validation failure or business rule violation
    /// 409 Conflict — same RequestId, different payload
    /// </summary>
    [HttpPost("orders")]
    public async Task<IActionResult> CreateOrder(
        [FromBody] CreateZoneFulfillmentOrderRequest req,
        CancellationToken ct)
    {
        if (!IsExperimentalRequest())
            return StatusCode(403, new { error = "X-Zone-Experimental: true header required." });

        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        try
        {
            var result = await _orch.OrchestrateAsync(req, ct);

            var resp = BuildResponse(result);

            return result.IsNew
                ? StatusCode(201, resp)
                : Ok(resp);
        }
        catch (ZoneFulfillmentException ex) when
            (ex.FailureKind == ZoneFulfillmentFailureKind.IdempotencyConflict)
        {
            return Conflict(new { error = ex.Message, failureKind = ex.FailureKind });
        }
        catch (ZoneFulfillmentException ex) when
            (ex.FailureKind == ZoneFulfillmentFailureKind.SapUnknownOutcome)
        {
            _log.LogWarning("[ZF-Ctrl] UnknownOutcome RequestId={Rid}", req.RequestId);
            return Accepted(new { requestId = req.RequestId, state = OrchestrationState.UnknownOutcome,
                message = "SAP outcome uncertain — poll GET status endpoint." });
        }
        catch (ZoneFulfillmentException ex)
        {
            return BadRequest(new { error = ex.Message, failureKind = ex.FailureKind });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[ZF-Ctrl] Unhandled exception RequestId={Rid}", req.RequestId);
            return StatusCode(500, new { error = "Internal error. See logs." });
        }
    }

    // ── C15: Status ────────────────────────────────────────────────────────────

    /// <summary>GET /api/zone-fulfillment/experimental/orders/{requestId}/status</summary>
    [HttpGet("orders/{requestId:guid}/status")]
    public async Task<IActionResult> GetStatus(Guid requestId, CancellationToken ct)
    {
        if (!IsExperimentalRequest())
            return StatusCode(403, new { error = "X-Zone-Experimental: true header required." });

        var orch = await _repo.FindOrchestrationAsync(requestId, ct);
        if (orch is null)
            return NotFound(new { error = $"RequestId {requestId} not found." });

        var lines  = await _repo.GetRequestLinesAsync(requestId, ct);
        var frags  = await _repo.GetSoLineFragmentsAsync(orch.Id, ct);

        var lineMap = lines.ToDictionary(l => l.RequestLineId);
        var fragMap = frags.GroupBy(f => f.RequestLineId)
                          .ToDictionary(g => g.Key, g => g.ToList());

        var lineSummaries = lines.OrderBy(l => l.LineSeq).Select(l =>
        {
            var lineFrags = fragMap.TryGetValue(l.RequestLineId, out var lf) ? lf : [];
            return new RequestLineSummary
            {
                RequestLineId = l.RequestLineId,
                LineSeq       = l.LineSeq,
                ItemCode      = l.ItemCode,
                RequestedQty  = l.RequestedQty,
                Fragments     = lineFrags.Select(f => new AllocationFragmentSummary
                {
                    WhsCode        = f.WhsCode,
                    AllocatedQty   = f.AllocatedQty,
                    UnallocatedQty = f.UnallocatedQty,
                    SoLineQty      = f.SoLineQty,
                    SoLineNum      = f.SoLineNum
                }).ToList()
            };
        }).ToList();

        return Ok(new ZoneFulfillmentStatusResponse
        {
            RequestId         = orch.RequestId,
            State             = orch.State,
            DeliveryLocation  = orch.DeliveryLocation,
            SoDocEntry        = orch.SoDocEntry,
            SoDocNum          = orch.SoDocNum,
            AllocationVersion = orch.AllocationVersion,
            FailureKind       = orch.FailureKind,
            ErrorMessage      = orch.ErrorMessage,
            CreatedAtUtc      = orch.CreatedAtUtc,
            UpdatedAtUtc      = orch.UpdatedAtUtc,
            Lines             = lineSummaries
        });
    }

    // ── C16: Dry-run plan (non-reserving) ─────────────────────────────────────

    /// <summary>
    /// POST /api/zone-fulfillment/experimental/plan
    /// Returns the allocation plan without creating any SO or reserving any stock.
    /// For planning/preview only. Stock may change before real order creation.
    /// </summary>
    [HttpPost("plan")]
    public async Task<IActionResult> GetPlan(
        [FromBody] CreateZoneFulfillmentOrderRequest req,
        CancellationToken ct)
    {
        if (!IsExperimentalRequest())
            return StatusCode(403, new { error = "X-Zone-Experimental: true header required." });

        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        try
        {
            var zone = await _repo.GetZoneWarehousesAsync(req.DeliveryLocation, ct);
            if (zone.Count == 0)
                return BadRequest(new { error = $"DeliveryLocation '{req.DeliveryLocation}' not found." });

            var itemCodes = req.Lines.Select(l => l.ItemCode).Distinct().ToList();
            var snapshots = _oitw.GetSnapshots(itemCodes, zone);

            var domainLines = req.Lines.Select(l => new DomainRequestLine(
                l.RequestLineId, l.LineSeq, l.ItemCode,
                l.RequestedQty, l.UnitPrice,
                l.Description, l.U_ItemName, l.U_Manufacturer)).ToList();

            var allocation = _allocator.Allocate(zone, domainLines, snapshots);

            var linesByReqLine = domainLines.ToDictionary(l => l.RequestLineId);

            var planLines = domainLines.OrderBy(l => l.LineSeq).Select(l =>
            {
                var lineFrags = allocation.ForLine(l.RequestLineId).Select(f =>
                    new AllocationFragmentSummary
                    {
                        WhsCode        = f.WhsCode,
                        AllocatedQty   = f.AllocatedQty,
                        UnallocatedQty = f.UnallocatedQty,
                        SoLineQty      = f.SoLineQty
                    }).ToList();

                return new RequestLinePlan
                {
                    RequestLineId = l.RequestLineId,
                    LineSeq       = l.LineSeq,
                    ItemCode      = l.ItemCode,
                    RequestedQty  = l.RequestedQty,
                    Fragments     = lineFrags
                };
            }).ToList();

            return Ok(new ZonePlanResponse
            {
                RequestId        = req.RequestId,
                DeliveryLocation = req.DeliveryLocation,
                HasShortage      = allocation.HasShortage,
                Lines            = planLines
            });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[ZF-Ctrl] Plan endpoint error RequestId={Rid}", req.RequestId);
            return StatusCode(500, new { error = "Internal error computing plan." });
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private bool IsExperimentalRequest() =>
        Request.Headers.TryGetValue("X-Zone-Experimental", out var val)
        && val.ToString().Equals("true", StringComparison.OrdinalIgnoreCase);

    private static CreateZoneFulfillmentOrderResponse BuildResponse(OrchestrateResult result)
    {
        var frags = result.Fragments.Select(f => new AllocationFragmentSummary
        {
            WhsCode        = f.WhsCode,
            AllocatedQty   = f.AllocatedQty,
            UnallocatedQty = f.UnallocatedQty,
            SoLineQty      = f.SoLineQty
        }).ToList();

        return new CreateZoneFulfillmentOrderResponse
        {
            RequestId        = result.Orch.RequestId,
            State            = result.Orch.State,
            SoDocEntry       = result.Orch.SoDocEntry,
            SoDocNum         = result.Orch.SoDocNum,
            DeliveryLocation = result.Orch.DeliveryLocation,
            HasShortage      = result.HasShortage,
            Fragments        = frags
        };
    }
}
