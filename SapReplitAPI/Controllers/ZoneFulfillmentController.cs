using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
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
    private readonly ZoneFulfillmentPickListService      _pickList;
    private readonly ZoneFulfillmentDeliveryService      _delivery;
    private readonly SapService                          _sap;
    private readonly ZoneFulfillmentOptions              _zfOpts;
    private readonly ILogger<ZoneFulfillmentController>  _log;

    public ZoneFulfillmentController(
        ZoneFulfillmentOrchestrationService orch,
        ZoneFulfillmentRepository           repo,
        ZoneAllocationEngine                allocator,
        SapOitwAdapter                      oitw,
        ZoneFulfillmentPickListService      pickList,
        ZoneFulfillmentDeliveryService      delivery,
        SapService                          sap,
        IOptions<ZoneFulfillmentOptions>    zfOptions,
        ILogger<ZoneFulfillmentController>  log)
    {
        _orch      = orch;
        _repo      = repo;
        _allocator = allocator;
        _sap       = sap;
        _oitw      = oitw;
        _pickList  = pickList;
        _delivery  = delivery;
        _zfOpts    = zfOptions.Value;
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
            // Resolve effective DeliveryLocation (blank → configured DefaultZone)
            var (effectiveZone, defaultApplied, zoneError) =
                await ResolveDeliveryLocationAsync(req.DeliveryLocation, ct);
            if (zoneError is not null)
                return BadRequest(new { error = zoneError });

            // Rebuild request with effective zone so orchestration persists the correct value
            var effectiveReq = new CreateZoneFulfillmentOrderRequest
            {
                RequestId        = req.RequestId,
                CardCode         = req.CardCode,
                DocDate          = req.DocDate,
                DeliveryDate     = req.DeliveryDate,
                DeliveryLocation = effectiveZone,
                SlpCode          = req.SlpCode,
                Lines            = req.Lines
            };

            var result = await _orch.OrchestrateAsync(effectiveReq, ct);
            var resp   = BuildResponse(result);
            if (defaultApplied)
                _log.LogInformation("[ZF-Ctrl] DefaultZone applied: '{Zone}' RequestId={Rid}",
                    effectiveZone, req.RequestId);

            return result.IsNew ? StatusCode(201, resp) : Ok(resp);
        }
        catch (ZoneFulfillmentShortageException ex)
        {
            // Hard-fail: insufficient stock — HTTP 422 Unprocessable Entity
            _log.LogWarning("[ZF-Ctrl] InsufficientStock RequestId={Rid}: {Msg}", req.RequestId, ex.Message);
            return UnprocessableEntity(new
            {
                failureKind      = ex.FailureKind,
                reason           = "INSUFFICIENT_STOCK_ACROSS_ALL_WAREHOUSES",
                deliveryLocation = req.DeliveryLocation,
                shortages        = ex.Shortages.Select(s => new
                {
                    itemCode           = s.ItemCode,
                    requestedQty       = s.RequestedQty,
                    availableQty       = s.AvailableQty,
                    shortageQty        = s.ShortageQty,
                    warehousesExamined = s.WarehousesExamined
                })
            });
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
            // Resolve effective DeliveryLocation (blank → configured DefaultZone)
            var (effectiveZone, defaultApplied, zoneError) =
                await ResolveDeliveryLocationAsync(req.DeliveryLocation, ct);
            if (zoneError is not null)
                return BadRequest(new { error = zoneError });

            var zone = await _repo.GetZoneWarehousesAsync(effectiveZone, ct);
            if (zone.Count == 0)
                return BadRequest(new { error = $"DeliveryLocation '{effectiveZone}' not found in ZoneWarehousePriority." });

            var itemCodes = req.Lines.Select(l => l.ItemCode).Distinct().ToList();
            var snapshots = _oitw.GetSnapshots(itemCodes, zone);

            var domainLines = req.Lines.Select(l => new DomainRequestLine(
                l.RequestLineId, l.LineSeq, l.ItemCode,
                l.RequestedQty, l.UnitPrice,
                l.Description, l.U_ItemName, l.U_Manufacturer)).ToList();

            var allocation = _allocator.Allocate(zone, domainLines, snapshots);

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
                DeliveryLocation = effectiveZone,
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

    // ── C-SAP-02: Create Pick List ────────────────────────────────────────────

    /// <summary>
    /// POST /api/zone-fulfillment/experimental/orders/{requestId}/pick-lists
    /// 201 Created  — new OPKL(s) created and persisted
    /// 200 OK       — idempotent (pick lists already exist for this RequestId)
    /// 404 Not Found — RequestId unknown or has no SO
    /// </summary>
    [HttpPost("orders/{requestId:guid}/pick-lists")]
    public async Task<IActionResult> CreatePickLists(Guid requestId, CancellationToken ct)
    {
        if (!IsExperimentalRequest())
            return StatusCode(403, new { error = "X-Zone-Experimental: true header required." });

        try
        {
            var result = await _pickList.CreatePickListsAsync(requestId, ct);

            var resp = new
            {
                requestId  = result.RequestId,
                isNew      = result.IsNew,
                pickLists  = result.PickLists.Select(pl => new
                {
                    pickListAbsEntry = pl.PickListAbsEntry,
                    whsCode          = pl.WhsCode,
                    soDocEntry       = pl.SoDocEntry,
                    soLineNum        = pl.SoLineNum,
                    releasedQty      = pl.ReleasedQty,
                    status           = pl.Status,
                    isNew            = pl.IsNew,
                    lineDescription  = pl.LineDescription
                }).ToList()
            };

            return result.IsNew ? StatusCode(201, resp) : Ok(resp);
        }
        catch (InvalidOperationException ex)
        {
            _log.LogWarning("[ZF-Ctrl-PL] InvalidOperation RequestId={Rid}: {Msg}", requestId, ex.Message);
            return NotFound(new { error = ex.Message });
        }
        catch (SapPickListAddException ex)
        {
            _log.LogError("[ZF-Ctrl-PL] SAP OPKL.Add() definitive failure RequestId={Rid} rc={Rc}: {Err}",
                requestId, ex.Rc, ex.SapError);
            return StatusCode(500, new { error = "SAP pick list creation failed.", detail = ex.SapError });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[ZF-Ctrl-PL] Unhandled exception RequestId={Rid}", requestId);
            return StatusCode(500, new { error = "Internal error. See logs." });
        }
    }

    // ── C-SAP-03: Pick execution ──────────────────────────────────────────────

    /// <summary>
    /// GET /api/zone-fulfillment/experimental/orders/{requestId}/pick-lists/{absEntry}/state
    /// Returns a read-only pre-mutation gate snapshot: MolasIntegration + SAP PKL1 + ORDR UDFs + OITW + OIBQ bins.
    /// Does not mutate SAP. Used for Section 17 pre-mutation gate verification.
    /// </summary>
    [HttpGet("orders/{requestId:guid}/pick-lists/{absEntry:int}/state")]
    public async Task<IActionResult> GetPickListState(Guid requestId, int absEntry, CancellationToken ct)
    {
        if (!IsExperimentalRequest())
            return StatusCode(403, new { error = "X-Zone-Experimental: true header required." });

        try
        {
            var snap = await _pickList.GetPickListStateAsync(requestId, absEntry, ct);

            return Ok(new
            {
                requestId        = snap.RequestId,
                pickListAbsEntry = snap.PickListAbsEntry,
                molasRecord = new
                {
                    id               = snap.MolasRecord.Id,
                    soDocEntry       = snap.MolasRecord.SoDocEntry,
                    soLineNum        = snap.MolasRecord.SoLineNum,
                    whsCode          = snap.MolasRecord.WhsCode,
                    releasedQty      = snap.MolasRecord.ReleasedQty,
                    pickedQty        = snap.MolasRecord.PickedQty,
                    status           = snap.MolasRecord.Status,
                    updatedAtUtc     = snap.MolasRecord.UpdatedAtUtc
                },
                sapPkl1 = snap.SapPkl1State is null ? null : new
                {
                    absEntry    = snap.SapPkl1State.AbsEntry,
                    orderEntry  = snap.SapPkl1State.OrderEntry,
                    orderLine   = snap.SapPkl1State.OrderLine,
                    baseObject  = snap.SapPkl1State.BaseObject,
                    relQtty     = snap.SapPkl1State.RelQtty,
                    pickQtty    = snap.SapPkl1State.PickQtty,
                    pickStatus  = snap.SapPkl1State.PickStatus
                },
                sapSoUdfs = snap.SapSoUdfs is null ? null : new
                {
                    uZoneRef         = snap.SapSoUdfs.UZoneRef,
                    uDeliveryLocation = snap.SapSoUdfs.UDeliveryLocation,
                    uReplitId        = snap.SapSoUdfs.UReplitId,
                    docStatus        = snap.SapSoUdfs.DocStatus,
                    canceled         = snap.SapSoUdfs.Canceled
                },
                sapOitw = snap.SapOitw is null ? null : new
                {
                    itemCode    = snap.SapOitw.ItemCode,
                    whsCode     = snap.SapOitw.WhsCode,
                    onHand      = snap.SapOitw.OnHand,
                    isCommited  = snap.SapOitw.IsCommited,
                    onOrder     = snap.SapOitw.OnOrder
                },
                sapBins = snap.SapBins.Select(b => new
                {
                    binAbsEntry = b.BinAbsEntry,
                    binCode     = b.BinCode,
                    qty         = b.Qty
                }).ToList()
            });
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[ZF-Ctrl-PICK] GetPickListState error RequestId={Rid} AbsEntry={Abs}", requestId, absEntry);
            return StatusCode(500, new { error = "Internal error. See logs." });
        }
    }

    /// <summary>
    /// POST /api/zone-fulfillment/experimental/orders/{requestId}/pick-lists/{absEntry}/pick
    /// Executes a controlled pick on the specified OPKL.
    ///
    /// C-SAP-03 constraints (enforced in service):
    ///   - desiredPickedQty must equal ReleasedQty (full-pick only; partial pick deferred)
    ///   - Desired-state semantics: replay with same qty returns 200 AlreadyApplied=true
    ///   - BLOCKED: DO NOT call this until Section 18 decision matrix passes and
    ///     explicit authorization to execute SAP pl.Update() is granted.
    ///
    /// 200 OK            — AlreadyApplied or RecoveredFromSap (no SAP mutation)
    /// 201 Created       — Fresh pick executed successfully
    /// 400 Bad Request   — Validation failure or full-pick enforcement
    /// 404 Not Found     — RequestId or AbsEntry not found
    /// 500 Internal      — SAP DI API failure
    /// </summary>
    [HttpPost("orders/{requestId:guid}/pick-lists/{absEntry:int}/pick")]
    public async Task<IActionResult> ExecutePick(
        Guid requestId, int absEntry,
        [FromBody] ExecutePickRequest req,
        CancellationToken ct)
    {
        if (!IsExperimentalRequest())
            return StatusCode(403, new { error = "X-Zone-Experimental: true header required." });

        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        try
        {
            var result = await _pickList.ExecutePickAsync(requestId, absEntry, req.DesiredPickedQty, ct);

            var resp = new
            {
                requestId        = result.RequestId,
                pickListAbsEntry = result.PickListAbsEntry,
                releasedQty      = result.ReleasedQty,
                pickedQty        = result.PickedQty,
                status           = result.Status,
                isNew            = result.IsNew,
                alreadyApplied   = result.AlreadyApplied,
                recoveredFromSap = result.RecoveredFromSap,
                sapPostState     = result.SapPostState is null ? null : new
                {
                    pickQtty    = result.SapPostState.PickQtty,
                    pickStatus  = result.SapPostState.PickStatus,
                    relQtty     = result.SapPostState.RelQtty
                },
                binsUsed = result.BinsUsed.Select(b => new
                {
                    binAbsEntry = b.BinAbsEntry,
                    binCode     = b.BinCode,
                    qty         = b.Qty
                }).ToList()
            };

            return result.IsNew ? StatusCode(201, resp) : Ok(resp);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            _log.LogWarning("[ZF-Ctrl-PICK] ExecutePick not found RequestId={Rid} AbsEntry={Abs}: {Msg}",
                requestId, absEntry, ex.Message);
            return NotFound(new { error = ex.Message });
        }
        catch (SapPickListUpdateException ex)
        {
            _log.LogError("[ZF-Ctrl-PICK] SAP pl.Update() failed RequestId={Rid} AbsEntry={Abs} rc={Rc}: {Err}",
                requestId, absEntry, ex.SapErrorCode, ex.Message);
            return StatusCode(500, new { error = "SAP pick list update failed.", detail = ex.Message, sapErrorCode = ex.SapErrorCode });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[ZF-Ctrl-PICK] Unhandled exception RequestId={Rid} AbsEntry={Abs}", requestId, absEntry);
            return StatusCode(500, new { error = "Internal error. See logs." });
        }
    }

    // ── Delivery gate diagnostics (read-only, no SAP mutation) ────────────────

    /// <summary>
    /// GET /api/zone-fulfillment/experimental/delivery-gate/diagnostics
    /// READ-ONLY. Queries live SAP B1 and MolasIntegration to support the
    /// delivery gate pre-mutation report. No document is created or modified.
    /// X-Zone-Experimental: true required.
    /// </summary>
    [HttpGet("delivery-gate/diagnostics")]
    public IActionResult GetDeliveryGateDiagnostics(
        [FromQuery] int pickListAbsEntry = 8,
        [FromQuery] int soDocEntry       = 28431,
        [FromQuery] int soLineNum        = 0,
        [FromQuery] string itemCode      = "BM10530",
        [FromQuery] string whsCode       = "003")
    {
        if (!IsExperimentalRequest())
            return StatusCode(403, new { error = "X-Zone-Experimental: true header required." });

        try
        {
            var d = _sap.GetDeliveryGateDiagnostics(pickListAbsEntry, soDocEntry, soLineNum, itemCode, whsCode);
            return Ok(new
            {
                odlnUdfColumns     = d.OdlnUdfColumns.Select(c => new { c.Name, c.SqlType, c.IsNullable, c.MaxLength }),
                pklTableNames      = d.PklTableNames,
                pklColumnDetails   = d.PklColumnDetails.Select(c => new { c.Table, c.Column, c.SqlType, c.MaxLength }),
                pkl1Rows           = d.Pkl1Rows.Select(r => new { r.AbsEntry, r.PickEntry, r.OrderEntry, r.OrderLine, r.PickQtty, r.RelQtty, r.PickStatus }),
                oibqRows           = d.OibqRows.Select(r => new { r.BinAbsEntry, r.BinCode, r.OnHandQty }),
                rdr1Dscription     = d.Rdr1Dscription,
                rdr1OpenQty        = d.Rdr1OpenQty,
                multiWhsDeliveries = d.MultiWhsDeliveries.Select(m => new { m.DocEntry, m.WhsCount, m.WhsCodes }),
                diApiPickList      = new
                {
                    loaded     = d.DiApiPickListLoaded,
                    binCount   = d.DiApiPickBinCount,
                    binRows    = d.DiApiPickBinRows.Select(b => new { b.BinAbsEntry, b.Quantity }),
                    error      = d.DiApiPickListError
                }
            });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[ZF-Ctrl-DIAG] DeliveryGateDiagnostics failed");
            return StatusCode(500, new { error = "Internal error. See logs.", detail = ex.Message });
        }
    }

    // ── Delivery gate: pre-mutation read-only preflight ───────────────────────

    /// <summary>
    /// GET /api/zone-fulfillment/experimental/orders/{requestId}/delivery/preflight
    /// READ-ONLY. Returns the full pre-mutation delivery gate state for the given RequestId.
    /// No SAP document is created or modified.
    /// </summary>
    [HttpGet("orders/{requestId:guid}/delivery/preflight")]
    public async Task<IActionResult> GetDeliveryPreflight(Guid requestId, CancellationToken ct)
    {
        if (!IsExperimentalRequest())
            return StatusCode(403, new { error = "X-Zone-Experimental: true header required." });

        try
        {
            var p = await _delivery.PreflightAsync(requestId, ct);
            return Ok(BuildPreflightResponse(p));
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[ZF-Ctrl-DLV] Preflight error RequestId={Rid}", requestId);
            return StatusCode(500, new { error = "Internal error. See logs." });
        }
    }

    /// <summary>
    /// POST /api/zone-fulfillment/experimental/orders/{requestId}/delivery
    /// Executes delivery orchestration with full idempotency and gate validation.
    /// Returns 201 on DELIVERY_CREATED, 200 on idempotent, 422 on gate errors.
    /// </summary>
    [HttpPost("orders/{requestId:guid}/delivery")]
    public async Task<IActionResult> ExecuteDelivery(
        Guid requestId,
        [FromBody] ExecuteDeliveryRequest req,
        CancellationToken ct)
    {
        if (!IsExperimentalRequest())
            return StatusCode(403, new { error = "X-Zone-Experimental: true header required." });

        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        try
        {
            var result  = await _delivery.ExecuteDeliveryAsync(requestId, ct);
            var preflight = result.Preflight;
            var record    = result.Record;
            var verdict   = result.GateVerdict;
            var odln      = result.Odln;

            var resp = new
            {
                requestId       = requestId,
                gateVerdict     = verdict,
                mutationEnabled = true,
                message         = verdict switch
                {
                    "DELIVERY_CREATED"
                        => $"ODLN created. DocEntry={odln?.DocEntry} DocNum={odln?.DocNum}.",
                    "IDEMPOTENT_SAP_ODLN_EXISTS"
                        => $"Idempotent: SAP ODLN already exists DocEntry={odln?.DocEntry}.",
                    "IDEMPOTENT_DELIVERY_RECORD_CREATED"
                        => $"Idempotent: DeliveryRecord already Created SapDocEntry={record?.SapDocEntry}.",
                    "GATE_ERRORS_BLOCK_MUTATION"
                        => $"Gate errors block mutation: {preflight.GateErrors.Count} error(s).",
                    "LIVE_RECHECK_FAILED"
                        => "Pre-Add live re-check failed. Delivery aborted. See DeliveryRecord.SapErrorMessage.",
                    "CONCURRENCY_CAUGHT_BY_UNIQUE_CONSTRAINT"
                        => "Concurrent delivery attempt detected — unique constraint caught it safely.",
                    _ => verdict
                },
                deliveryRecord = record is null ? null : new
                {
                    id               = record.Id,
                    status           = record.Status,
                    sapDocEntry      = record.SapDocEntry,
                    sapDocNum        = record.SapDocNum,
                    cardCode         = record.CardCode,
                    deliveryLocation = record.DeliveryLocation
                },
                odln = odln is null ? null : new
                {
                    docEntry          = odln.DocEntry,
                    docNum            = odln.DocNum,
                    docStatus         = odln.DocStatus,
                    canceled          = odln.Canceled,
                    cardCode          = odln.CardCode,
                    uZoneRef          = odln.UZoneRef,
                    uDeliveryLocation = odln.UDeliveryLocation,
                    uReplitId         = odln.UReplitId,
                    elapsedMs         = odln.ElapsedMs,
                    lines             = odln.Lines.Select(l => new
                    {
                        lineNum    = l.LineNum,
                        itemCode   = l.ItemCode,
                        quantity   = l.Quantity,
                        whsCode    = l.WhsCode,
                        baseType   = l.BaseType,
                        baseEntry  = l.BaseEntry,
                        baseLine   = l.BaseLine,
                        dscription = l.Dscription
                    }),
                    binAllocations = odln.BinAllocations.Select(b => new
                    {
                        binAbsEntry = b.BinAbsEntry,
                        binCode     = b.BinCode,
                        qty         = b.Qty
                    })
                },
                preflight = BuildPreflightResponse(preflight)
            };

            int statusCode = verdict switch
            {
                "DELIVERY_CREATED"                     => 201,
                "IDEMPOTENT_SAP_ODLN_EXISTS"           => 200,
                "IDEMPOTENT_DELIVERY_RECORD_CREATED"   => 200,
                "GATE_ERRORS_BLOCK_MUTATION"           => 422,
                "LIVE_RECHECK_FAILED"                  => 409,
                "CONCURRENCY_CAUGHT_BY_UNIQUE_CONSTRAINT" => 409,
                _                                      => 500
            };

            return StatusCode(statusCode, resp);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            _log.LogError(ex, "[ZF-Ctrl-DLV] ExecuteDelivery InvalidOp RequestId={Rid}", requestId);
            return StatusCode(500, new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[ZF-Ctrl-DLV] ExecuteDelivery error RequestId={Rid}", requestId);
            return StatusCode(500, new { error = "Internal error. See logs." });
        }
    }

    /// <summary>
    /// GET /api/zone-fulfillment/experimental/debug/odln-bins/{docEntry}
    /// READ-ONLY diagnostic: probes candidate SAP tables to find the correct
    /// delivery bin allocation table for ODLN DocEntry. Does not modify any data.
    /// </summary>
    [HttpGet("debug/odln-bins/{docEntry:int}")]
    public IActionResult DebugOdlnBins(int docEntry)
    {
        if (!IsExperimentalRequest())
            return StatusCode(403, new { error = "X-Zone-Experimental: true header required." });

        var results = _sap.ProbeDeliveryBinTables(docEntry);
        return Ok(results);
    }

    // ── C2: Picker assignment (read-only) ──────────────────────────────────────

    /// <summary>
    /// GET /api/zone-fulfillment/experimental/pickers
    /// Returns picker assignments for all 4 warehouses with SAP OUSR validation.
    /// READ-ONLY — does not create or modify any SAP document.
    /// </summary>
    [HttpGet("pickers")]
    public async Task<IActionResult> GetAllPickers(
        [FromServices] PickerResolutionService picker,
        CancellationToken ct)
    {
        if (!IsExperimentalRequest())
            return StatusCode(403, new { error = "X-Zone-Experimental: true header required." });

        var warehouses = new[] { "001", "002", "003", "004" };
        var results    = new List<object>();

        foreach (var whs in warehouses)
        {
            try
            {
                var r = await picker.ResolveAsync(whs, ct);
                results.Add(new
                {
                    whsCode    = whs,
                    status     = "ok",
                    sapUserId  = r.SapUser.UserId,
                    userCode   = r.SapUser.UserCode,
                    userName   = r.SapUser.UserName,
                    locked     = r.SapUser.Locked,
                    assignment = new
                    {
                        id        = r.Assignment.Id,
                        isActive  = r.Assignment.IsActive,
                        isDefault = r.Assignment.IsDefault
                    }
                });
            }
            catch (PickerAssignmentNotFoundException ex)
            {
                results.Add(new { whsCode = whs, status = "not_found", error = ex.Message });
            }
            catch (PickerAssignmentInvalidException ex)
            {
                results.Add(new { whsCode = whs, status = "invalid", error = ex.Message });
            }
            catch (PickerSapUserUnavailableException ex)
            {
                results.Add(new { whsCode = whs, status = "sap_user_unavailable", error = ex.Message });
            }
        }

        return Ok(new { pickers = results });
    }

    /// <summary>
    /// GET /api/zone-fulfillment/experimental/pickers/{whsCode}
    /// Returns the resolved and SAP-validated picker for one warehouse.
    /// READ-ONLY — does not create or modify any SAP document.
    /// </summary>
    [HttpGet("pickers/{whsCode}")]
    public async Task<IActionResult> GetPicker(
        string whsCode,
        [FromServices] PickerResolutionService picker,
        CancellationToken ct)
    {
        if (!IsExperimentalRequest())
            return StatusCode(403, new { error = "X-Zone-Experimental: true header required." });

        try
        {
            var r = await picker.ResolveAsync(whsCode, ct);
            return Ok(new
            {
                whsCode    = whsCode,
                status     = "ok",
                sapUserId  = r.SapUser.UserId,
                userCode   = r.SapUser.UserCode,
                userName   = r.SapUser.UserName,
                locked     = r.SapUser.Locked,
                assignment = new
                {
                    id        = r.Assignment.Id,
                    isActive  = r.Assignment.IsActive,
                    isDefault = r.Assignment.IsDefault
                }
            });
        }
        catch (PickerAssignmentNotFoundException ex)
        {
            return NotFound(new { whsCode, error = ex.Message });
        }
        catch (PickerAssignmentInvalidException ex)
        {
            return UnprocessableEntity(new { whsCode, error = ex.Message });
        }
        catch (PickerSapUserUnavailableException ex)
        {
            return StatusCode(503, new { whsCode, error = ex.Message });
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves the effective delivery zone for both /orders and /plan endpoints.
    ///
    /// Rules (S10):
    ///   1. Blank/whitespace → use configured DefaultZone (fail closed if DefaultZone is blank or invalid).
    ///   2. Non-blank → use as-is (validated by orchestration service's zone lookup).
    ///
    /// Returns (effectiveZone, defaultApplied, errorMessage).
    /// errorMessage is non-null only when resolution fails closed (bad config).
    /// </summary>
    private async Task<(string Effective, bool DefaultApplied, string? Error)>
        ResolveDeliveryLocationAsync(string raw, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(raw))
            return (raw.Trim(), false, null);

        // Blank → fall through to configured DefaultZone
        string defaultZone = _zfOpts.DefaultZone;

        if (string.IsNullOrWhiteSpace(defaultZone))
        {
            _log.LogError("[ZF-Ctrl] DefaultZone is blank/missing in ZoneFulfillment config — fail closed.");
            return ("", false, "ZoneFulfillment:DefaultZone is not configured. " +
                "Provide a DeliveryLocation or configure a default zone.");
        }

        // Validate DefaultZone exists in ZoneWarehousePriority (fail closed on bad config)
        var rows = await _repo.GetZoneWarehousesAsync(defaultZone, ct);
        if (rows.Count == 0)
        {
            _log.LogError("[ZF-Ctrl] DefaultZone '{Zone}' has no active rows in ZoneWarehousePriority — fail closed.",
                defaultZone);
            return ("", false,
                $"Configuration error: ZoneFulfillment:DefaultZone='{defaultZone}' " +
                $"is not a known active zone. Fix appsettings or supply an explicit DeliveryLocation.");
        }

        _log.LogInformation("[ZF-Ctrl] Blank DeliveryLocation → DefaultZone='{Zone}'", defaultZone);
        return (defaultZone, true, null);
    }

    private bool IsExperimentalRequest() =>
        Request.Headers.TryGetValue("X-Zone-Experimental", out var val)
        && val.ToString().Equals("true", StringComparison.OrdinalIgnoreCase);

    private static object BuildPreflightResponse(SapReplitAPI.Models.ZoneFulfillment.DeliveryPreflightResult p) =>
        new
        {
            requestId            = p.RequestId,
            orchestration = new
            {
                requestId        = p.Orchestration.RequestId,
                state            = p.Orchestration.State,
                uReplitId        = p.Orchestration.U_ReplitId,
                soDocEntry       = p.Orchestration.SoDocEntry,
                deliveryLocation = p.Orchestration.DeliveryLocation
            },
            soUdfs = p.SoUdfs is null ? null : new
            {
                uZoneRef          = p.SoUdfs.UZoneRef,
                uDeliveryLocation = p.SoUdfs.UDeliveryLocation,
                uReplitId         = p.SoUdfs.UReplitId,
                docStatus         = p.SoUdfs.DocStatus,
                canceled          = p.SoUdfs.Canceled
            },
            fragments = p.Fragments.Select(f => new
            {
                fragmentId       = f.Fragment.Id,
                soDocEntry       = f.Fragment.SoDocEntry,
                soLineNum        = f.Fragment.SoLineNum,
                itemCode         = f.Fragment.ItemCode,
                whsCode          = f.Fragment.WhsCode,
                allocatedQty     = f.Fragment.AllocatedQty,
                releasedQty      = f.Fragment.ReleasedQty,
                pickedQty        = f.PickListRecord.PickedQty,
                deliveredQty     = f.Fragment.DeliveredQty,
                remainingPickedQty = f.RemainingPickedQty,
                rdr1OpenQty      = f.Rdr1OpenQty,
                pickListAbsEntry = f.PickListRecord.PickListAbsEntry,
                pickListStatus   = f.PickListRecord.Status,
                sapPkl1 = f.SapPkl1 is null ? null : new
                {
                    pickQtty   = f.SapPkl1.PickQtty,
                    relQtty    = f.SapPkl1.RelQtty,
                    pickStatus = f.SapPkl1.PickStatus
                },
                binAllocations = f.BinAllocations.Select(b => new
                {
                    binAbsEntry = b.BinAbsEntry,
                    binCode     = b.BinCode,
                    qty         = b.Qty
                }).ToList(),
                validationErrors = f.ValidationErrors
            }).ToList(),
            idempotency = new
            {
                existingDeliveryRecord = p.ExistingDeliveryRecord is null ? null : new
                {
                    id          = p.ExistingDeliveryRecord.Id,
                    status      = p.ExistingDeliveryRecord.Status,
                    sapDocEntry = p.ExistingDeliveryRecord.SapDocEntry,
                    sapDocNum   = p.ExistingDeliveryRecord.SapDocNum
                },
                existingSapOdlnDocEntry = p.ExistingSapDelivery?.DocEntry,
                existingSapOdlnDocNum   = p.ExistingSapDelivery?.DocNum
            },
            invoiceSafety = new
            {
                filterActive  = p.InvoiceFilterActive,
                filterSql     = "ISNULL(T0.U_ZoneRef,'') <> 'ZoneFulfillment'",
                verdict       = p.InvoiceFilterActive
                    ? "ZF deliveries excluded from InvoiceFromDeliveryJob"
                    : "WARNING: filter not verified"
            },
            gateErrors = p.GateErrors,
            gatePass   = p.GateErrors.Count == 0
        };

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
