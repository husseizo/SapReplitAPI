using Microsoft.Extensions.Logging;
using SapReplitAPI.DTOs.ZoneFulfillment;
using SapReplitAPI.Models.ZoneFulfillment;

namespace SapReplitAPI.Services.ZoneFulfillment;

/// <summary>
/// C13: Full orchestration service for zone-fulfillment order creation.
/// Implements the happy path, all idempotency guards, and UnknownOutcome recovery.
///
/// Entry point: OrchestrateAsync(request) → CreateZoneFulfillmentOrderResponse
///
/// State machine:
///   Received → Validating → Allocating → CreatingSalesOrder → SalesOrderCreated → Accepted
///   Any step failure → Failed (definitive) or UnknownOutcome (SAP outcome uncertain)
///
/// Idempotency:
///   - Same RequestId + same PayloadHash → return existing orchestration
///   - Same RequestId + different payload → 409 IdempotencyConflict
///   - Same RequestId in terminal state (Accepted/Failed) → return result
///   - Same RequestId in UnknownOutcome → attempt SAP recovery
///
/// OrderAllocationCoordinator is held across OITW read → allocate → persist → SAP Add().
/// NEVER held simultaneously with NeonInventoryWriteCoordinator or InventoryCacheWriteCoordinator.
/// Phase 2 event pipeline handles OITW refresh automatically via SapEventOutbox — do NOT trigger manually.
/// </summary>
public sealed class ZoneFulfillmentOrchestrationService
{
    private readonly ZoneFulfillmentRepository        _repo;
    private readonly PayloadHashService               _hasher;
    private readonly ZoneAllocationEngine             _allocator;
    private readonly SapOitwAdapter                   _oitw;
    private readonly ZoneFulfillmentSapOrderService   _sapOrder;
    private readonly OrderAllocationCoordinator       _coordinator;
    private readonly ZoneFulfillmentPickListService   _pickListService;
    private readonly ILogger<ZoneFulfillmentOrchestrationService> _log;

    public ZoneFulfillmentOrchestrationService(
        ZoneFulfillmentRepository        repo,
        PayloadHashService               hasher,
        ZoneAllocationEngine             allocator,
        SapOitwAdapter                   oitw,
        ZoneFulfillmentSapOrderService   sapOrder,
        OrderAllocationCoordinator       coordinator,
        ZoneFulfillmentPickListService   pickListService,
        ILogger<ZoneFulfillmentOrchestrationService> log)
    {
        _repo            = repo;
        _hasher          = hasher;
        _allocator       = allocator;
        _oitw            = oitw;
        _sapOrder        = sapOrder;
        _coordinator     = coordinator;
        _pickListService = pickListService;
        _log             = log;
    }

    // ── Public entry point ─────────────────────────────────────────────────────

    public async Task<OrchestrateResult> OrchestrateAsync(
        CreateZoneFulfillmentOrderRequest req,
        CancellationToken ct = default)
    {
        string payloadHash = _hasher.Compute(req);

        // ── Idempotency guard ────────────────────────────────────────────────
        var existing = await _repo.FindOrchestrationAsync(req.RequestId, ct);
        if (existing is not null)
            return await HandleExistingOrchestrationAsync(existing, req, payloadHash, ct);

        // ── Check payload hash conflict (same RequestId, different payload not yet persisted) ──
        // Not needed here — if orchestration is null, RequestId is brand new.

        // ── Durable request persistence (must succeed before any SAP mutation) ──
        string? storedHash = await _repo.GetPayloadHashAsync(req.RequestId, ct);
        if (storedHash is not null && storedHash != payloadHash)
        {
            _log.LogWarning("[ZF-Orch] IdempotencyConflict RequestId={Rid} stored≠new", req.RequestId);
            throw new ZoneFulfillmentException(
                ZoneFulfillmentFailureKind.IdempotencyConflict,
                "A different payload was previously submitted with this RequestId.");
        }

        // First time — persist request durably
        _log.LogInformation("[ZF-Orch] New request RequestId={Rid} CardCode={Card} Lines={N}",
            req.RequestId, req.CardCode, req.Lines.Count);

        var orch = await _repo.InsertRequestAsync(req, payloadHash, ct);

        return await ExecuteOrchestrationAsync(orch, req, ct);
    }

    // ── Existing-orchestration branching ───────────────────────────────────────

    private async Task<OrchestrateResult> HandleExistingOrchestrationAsync(
        FulfillmentOrchestrationRecord existing,
        CreateZoneFulfillmentOrderRequest req,
        string payloadHash,
        CancellationToken ct)
    {
        // Payload hash conflict
        string? storedHash = await _repo.GetPayloadHashAsync(req.RequestId, ct);
        if (storedHash is not null && storedHash != payloadHash)
        {
            _log.LogWarning("[ZF-Orch] IdempotencyConflict RequestId={Rid}", req.RequestId);
            throw new ZoneFulfillmentException(
                ZoneFulfillmentFailureKind.IdempotencyConflict,
                "A different payload was previously submitted with this RequestId.");
        }

        return existing.State switch
        {
            OrchestrationState.Accepted or
            OrchestrationState.SalesOrderCreated or
            OrchestrationState.Delivered =>
                OrchestrateResult.Idempotent(existing),

            OrchestrationState.Failed =>
                OrchestrateResult.Failed(existing),

            OrchestrationState.UnknownOutcome =>
                await RecoverUnknownOutcomeAsync(existing, req, ct),

            // Terminal in-progress states — re-drive the orchestration
            _ => await ExecuteOrchestrationAsync(existing, req, ct)
        };
    }

    // ── UnknownOutcome recovery ────────────────────────────────────────────────

    private async Task<OrchestrateResult> RecoverUnknownOutcomeAsync(
        FulfillmentOrchestrationRecord orch,
        CreateZoneFulfillmentOrderRequest req,
        CancellationToken ct)
    {
        _log.LogWarning("[ZF-Orch] Attempting UnknownOutcome recovery RequestId={Rid}", req.RequestId);

        string uReplitId = ZoneFulfillmentSapOrderService.BuildReplitId(req.RequestId);
        var found = _sapOrder.FindExistingOrder(uReplitId);

        if (found.HasValue)
        {
            _log.LogInformation(
                "[ZF-Orch] Recovery: SAP ORDR found DocEntry={DocEntry} DocNum={DocNum}",
                found.Value.DocEntry, found.Value.DocNum);

            await _repo.SetSalesOrderCreatedAsync(
                orch.Id, uReplitId, found.Value.DocEntry, found.Value.DocNum, ct);

            orch.State      = OrchestrationState.SalesOrderCreated;
            orch.SoDocEntry = found.Value.DocEntry;
            orch.SoDocNum   = found.Value.DocNum;

            return OrchestrateResult.Recovered(orch);
        }

        // SAP doesn't have the order → safe to retry
        _log.LogInformation("[ZF-Orch] Recovery: SAP ORDR not found — retrying orchestration.");
        return await ExecuteOrchestrationAsync(orch, req, ct);
    }

    // ── Main orchestration pipeline ────────────────────────────────────────────

    private async Task<OrchestrateResult> ExecuteOrchestrationAsync(
        FulfillmentOrchestrationRecord orch,
        CreateZoneFulfillmentOrderRequest req,
        CancellationToken ct)
    {
        try
        {
            // Step 1: Validate zone config
            await _repo.UpdateStateAsync(orch.Id, OrchestrationState.Validating, ct);

            var zone = await _repo.GetZoneWarehousesAsync(req.DeliveryLocation, ct);
            if (zone.Count == 0)
            {
                await FailAsync(orch.Id, ZoneFulfillmentFailureKind.ValidationFailure,
                    $"DeliveryLocation '{req.DeliveryLocation}' is not a known active zone.", ct);
                throw new ZoneFulfillmentException(
                    ZoneFulfillmentFailureKind.ValidationFailure,
                    $"DeliveryLocation '{req.DeliveryLocation}' not found in ZoneWarehousePriority.");
            }

            // Step 2: Acquire coordinator and run allocation
            await _repo.UpdateStateAsync(orch.Id, OrchestrationState.Allocating, ct);

            AllocationResult allocation;
            long planId;
            List<AllocationFragment> orderedFragments;
            OrchestrateResult orchResult;

            using (var lockCtx = await _coordinator.AcquireAsync(ct))
            {
                // Fresh OITW read inside the lock
                var itemCodes = req.Lines.Select(l => l.ItemCode).Distinct().ToList();
                var snapshots = _oitw.GetSnapshots(itemCodes, zone);

                // Build domain lines
                var domainLines = req.Lines.Select(l => new DomainRequestLine(
                    l.RequestLineId, l.LineSeq, l.ItemCode,
                    l.RequestedQty, l.UnitPrice,
                    l.Description, l.U_ItemName, l.U_Manufacturer)).ToList();

                allocation = _allocator.Allocate(zone, domainLines, snapshots);

                _log.LogInformation(
                    "[ZF-Orch] Allocation complete RequestId={Rid} fragments={N} hasShortage={S}",
                    req.RequestId, allocation.Fragments.Count, allocation.HasShortage);

                // ── SHORTAGE HARD-FAIL GATE ───────────────────────────────────
                // PDF contract: if any item cannot be fully supplied, reject
                // before reaching SAP. No ORDR.Add() on shortage.
                if (allocation.HasShortage)
                {
                    var itemByReqLine = domainLines.ToDictionary(l => l.RequestLineId, l => l.ItemCode);
                    var demandByItem  = domainLines
                        .GroupBy(l => l.ItemCode, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(g => g.Key, g => g.Sum(l => l.RequestedQty), StringComparer.OrdinalIgnoreCase);

                    var allocByItem = allocation.Fragments
                        .GroupBy(f => itemByReqLine.TryGetValue(f.RequestLineId, out var ic) ? ic : "")
                        .ToDictionary(g => g.Key, g => g.Sum(f => f.AllocatedQty), StringComparer.OrdinalIgnoreCase);

                    var shortages = demandByItem
                        .Where(kv => {
                            decimal alloc2 = allocByItem.GetValueOrDefault(kv.Key);
                            return alloc2 < kv.Value;
                        })
                        .Select(kv => new ShortageItemDetail
                        {
                            ItemCode           = kv.Key,
                            RequestedQty       = kv.Value,
                            AvailableQty       = allocByItem.GetValueOrDefault(kv.Key),
                            DeliveryLocation   = req.DeliveryLocation,
                            WarehousesExamined = zone.Select(w => w.WhsCode).ToList()
                        }).ToList();

                    await FailAsync(orch.Id, ZoneFulfillmentFailureKind.InsufficientStock,
                        $"INSUFFICIENT_STOCK_ACROSS_ALL_WAREHOUSES: {shortages.Count} item(s) short.", ct);

                    throw new ZoneFulfillmentShortageException(shortages, req.DeliveryLocation);
                }

                // Persist allocation plan
                planId = await _repo.InsertAllocationPlanAsync(
                    orch.Id, orch.AllocationVersion, "Initial", allocation.Fragments, ct);

                // Determine insertion order for RDR1
                var lineSeqById  = domainLines.ToDictionary(l => l.RequestLineId, l => l.LineSeq);
                var whsPriority  = zone.ToDictionary(w => w.WhsCode, w => w.Priority);
                orderedFragments = allocation.Fragments
                    .OrderBy(f => lineSeqById.TryGetValue(f.RequestLineId, out int s) ? s : int.MaxValue)
                    .ThenBy(f => whsPriority.TryGetValue(f.WhsCode, out int p) ? p : int.MaxValue)
                    .ToList();

                // Step 3: Create SAP Sales Order (inside coordinator lock)
                await _repo.UpdateStateAsync(orch.Id, OrchestrationState.CreatingSalesOrder, ct);

                SapSoResult sapResult;
                try
                {
                    sapResult = _sapOrder.CreateSalesOrder(
                        req.RequestId,
                        req.CardCode,
                        req.DocDate.ToDateTime(TimeOnly.MinValue),
                        req.DeliveryDate.ToDateTime(TimeOnly.MinValue),
                        req.SlpCode,
                        req.DeliveryLocation,
                        orderedFragments,
                        domainLines,
                        zone);
                }
                catch (SapOrderAddException ex)
                {
                    // SAP returned a definitive rc!=0 — mark failed
                    await FailAsync(orch.Id, ZoneFulfillmentFailureKind.SapDefinitiveFailure, ex.Message, ct);
                    throw new ZoneFulfillmentException(ZoneFulfillmentFailureKind.SapDefinitiveFailure, ex.Message, ex);
                }
                catch (Exception ex) when (ex is not ZoneFulfillmentException)
                {
                    // Unknown: SAP may or may not have committed
                    string uReplitId = ZoneFulfillmentSapOrderService.BuildReplitId(req.RequestId);
                    await _repo.SetUnknownOutcomeAsync(orch.Id, uReplitId, ct);
                    _log.LogError(ex,
                        "[ZF-Orch] UnknownOutcome RequestId={Rid} uReplitId={Rid2}",
                        req.RequestId, uReplitId);
                    throw new ZoneFulfillmentException(ZoneFulfillmentFailureKind.SapUnknownOutcome,
                        "SAP outcome unknown — retry or check status.", ex);
                }

                // Step 4: Persist SoLineFragments + transition state
                await _repo.SetSalesOrderCreatedAsync(
                    orch.Id,
                    ZoneFulfillmentSapOrderService.BuildReplitId(req.RequestId),
                    sapResult.DocEntry,
                    sapResult.DocNum,
                    ct);

                var lineFragments = ZoneFulfillmentSapOrderService.ReconcileRdr1(
                    orch.Id, planId, sapResult.DocEntry, orderedFragments, sapResult.Lines);

                await _repo.InsertSoLineFragmentsAsync(orch.Id, planId, lineFragments, ct);
                await _repo.UpdateStateAsync(orch.Id, OrchestrationState.Accepted, ct);

                orch.State      = OrchestrationState.Accepted;
                orch.SoDocEntry = sapResult.DocEntry;
                orch.SoDocNum   = sapResult.DocNum;

                _log.LogInformation(
                    "[ZF-Orch] Accepted RequestId={Rid} DocEntry={DocEntry} DocNum={DocNum}",
                    req.RequestId, sapResult.DocEntry, sapResult.DocNum);

                orchResult = OrchestrateResult.Created(orch, allocation.HasShortage, allocation.Fragments);
            } // coordinator lock released here

            // Auto pick list creation outside the coordinator lock.
            // ORDR is already committed — pick list failure does not roll back the SO.
            await AutoCreatePickListsAsync(req.RequestId, ct);

            return orchResult;
        }
        catch (ZoneFulfillmentException)
        {
            throw; // already handled above
        }
        catch (Exception ex)
        {
            await FailAsync(orch.Id, ZoneFulfillmentFailureKind.PersistenceFailure, ex.Message, ct);
            throw new ZoneFulfillmentException(ZoneFulfillmentFailureKind.PersistenceFailure, ex.Message, ex);
        }
    }

    private async Task AutoCreatePickListsAsync(Guid requestId, CancellationToken ct)
    {
        try
        {
            await _pickListService.CreatePickListsAsync(requestId, ct);
            _log.LogInformation("[ZF-Orch] Auto pick lists created RequestId={Rid}", requestId);
        }
        catch (Exception ex)
        {
            // Swallow — ORDR is already committed. Recovery via POST /pick-lists endpoint.
            _log.LogError(ex,
                "[ZF-Orch] Auto pick list creation failed RequestId={Rid} — ORDR safe, recover via /pick-lists",
                requestId);
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private async Task FailAsync(long orchId, string kind, string msg, CancellationToken ct)
    {
        try { await _repo.SetFailedAsync(orchId, kind, msg, ct); }
        catch (Exception ex)
        {
            _log.LogError(ex, "[ZF-Orch] Failed to persist failure state OrchId={Id}", orchId);
        }
    }
}

// ── Result discriminated union ─────────────────────────────────────────────────

public sealed class OrchestrateResult
{
    public enum Kind { Created, Idempotent, Recovered, Failed }

    public Kind Kind2                             { get; private init; }
    public FulfillmentOrchestrationRecord Orch    { get; private init; } = null!;
    public bool HasShortage                        { get; private init; }
    public IReadOnlyList<AllocationFragment> Fragments { get; private init; } = [];

    public static OrchestrateResult Created(
        FulfillmentOrchestrationRecord orch, bool hasShortage,
        IReadOnlyList<AllocationFragment> frags) =>
        new() { Kind2 = Kind.Created, Orch = orch, HasShortage = hasShortage, Fragments = frags };

    public static OrchestrateResult Idempotent(FulfillmentOrchestrationRecord orch) =>
        new() { Kind2 = Kind.Idempotent, Orch = orch };

    public static OrchestrateResult Recovered(FulfillmentOrchestrationRecord orch) =>
        new() { Kind2 = Kind.Recovered, Orch = orch };

    public static OrchestrateResult Failed(FulfillmentOrchestrationRecord orch) =>
        new() { Kind2 = Kind.Failed, Orch = orch };

    public bool IsNew       => Kind2 == Kind.Created;
    public bool WasExisting => Kind2 is Kind.Idempotent or Kind.Recovered;
}

// ── Domain exceptions ─────────────────────────────────────────────────────────

public class ZoneFulfillmentException(string failureKind, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public string FailureKind { get; } = failureKind;
}

/// <summary>
/// Thrown when allocation.HasShortage=true — before any SAP mutation.
/// Controller maps this to HTTP 422 with structured shortage payload.
/// </summary>
public sealed class ZoneFulfillmentShortageException : ZoneFulfillmentException
{
    public IReadOnlyList<ShortageItemDetail> Shortages { get; }

    public ZoneFulfillmentShortageException(
        IReadOnlyList<ShortageItemDetail> shortages,
        string deliveryLocation)
        : base(
            ZoneFulfillmentFailureKind.InsufficientStock,
            $"INSUFFICIENT_STOCK_ACROSS_ALL_WAREHOUSES: {shortages.Count} item(s) cannot be " +
            $"fully supplied for zone '{deliveryLocation}'.")
    {
        Shortages = shortages;
    }
}
