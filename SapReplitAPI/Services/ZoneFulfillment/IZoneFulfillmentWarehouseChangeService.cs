using SapReplitAPI.Models.Orde_Models;

namespace SapReplitAPI.Services.ZoneFulfillment;

/// <summary>
/// One warehouse-change candidate derived from comparing the DTO against
/// the current SoLineFragment state. Unchanged lines are excluded.
/// </summary>
public sealed record WarehouseLineChange(
    int    SoLineNum,
    long   SoLineFragmentId,
    string ItemCode,
    string CurrentWhsCode,
    string RequestedWhsCode
);

/// <summary>Result of PreflightAsync.</summary>
public sealed class WfPreflightResult
{
    public bool                            IsNotZf    { get; private init; }
    public bool                            IsNoOp     { get; private init; }
    public bool                            IsBlocked  { get; private init; }
    public bool                            IsPass     { get; private init; }
    public string?                         BlockCode  { get; private init; }
    public string?                         BlockReason { get; private init; }
    public int?                            BlockedLineNum { get; private init; }
    public IReadOnlyList<WarehouseLineChange> Changes { get; private init; } = [];

    public static WfPreflightResult NotZf()
        => new() { IsNotZf = true };

    public static WfPreflightResult NoOp()
        => new() { IsNoOp = true };

    public static WfPreflightResult Blocked(string code, string reason, int? lineNum = null)
        => new() { IsBlocked = true, BlockCode = code, BlockReason = reason, BlockedLineNum = lineNum };

    public static WfPreflightResult Pass(IReadOnlyList<WarehouseLineChange> changes)
        => new() { IsPass = true, Changes = changes };
}

/// <summary>Result of ApplyOperationalWarehouseChangesAsync per line.</summary>
public sealed class WhsLineApplyResult
{
    public int    SoLineNum          { get; init; }
    public bool   Applied            { get; init; }
    public bool   Skipped            { get; init; }   // already at target WHS (idempotent)
    public bool   ConcurrencyConflict { get; init; }  // optimistic lock missed (0 rows updated)
    public string? Error             { get; init; }
}

/// <summary>Aggregate result of ApplyOperationalWarehouseChangesAsync.</summary>
public sealed class WhsApplyResult
{
    public bool                           AllApplied        { get; init; }
    public bool                           SyncFailed        { get; init; }
    public string?                        SyncError         { get; init; }
    public IReadOnlyList<WhsLineApplyResult> LineResults    { get; init; } = [];
}

/// <summary>Result of the pre-delivery WHS consistency gate.</summary>
public sealed class WhsDeliveryGateResult
{
    public bool   Pass       { get; init; }
    public string? FailCode  { get; init; }
    public string? FailReason { get; init; }

    public static WhsDeliveryGateResult Ok()
        => new() { Pass = true };

    public static WhsDeliveryGateResult Fail(string code, string reason)
        => new() { Pass = false, FailCode = code, FailReason = reason };
}

/// <summary>
/// Owns the full lifecycle of ZF post-allocation warehouse reassignment:
/// pre-SAP validation, post-SAP operational sync, delivery WHS consistency
/// guard, and idempotent reconciliation of SAP-ahead state.
/// </summary>
public interface IZoneFulfillmentWarehouseChangeService
{
    /// <summary>
    /// Evaluates whether the requested DTO warehouse changes are safe for a ZF order.
    /// Must be called BEFORE SapService.UpdateOrder(). If Blocked, the SAP update
    /// must not proceed.
    /// Returns NotZf when the SO is not ZF-enrolled (caller proceeds normally).
    /// Returns NoOp when no WHS lines differ (caller proceeds normally).
    /// Returns Pass with the change list when all gates pass.
    /// Returns Blocked when any gate fails — caller must return 409.
    /// </summary>
    Task<WfPreflightResult> PreflightAsync(
        int            soDocEntry,
        UpdateOrderDto dto,
        CancellationToken ct);

    /// <summary>
    /// Applies the approved warehouse changes to SoLineFragment (WhsCode, audit columns).
    /// Called AFTER SapService.UpdateOrder() succeeds.
    /// Uses optimistic concurrency (AND WhsCode = current) per line.
    /// Partial failure is non-fatal to the HTTP response but is logged
    /// with WAREHOUSE_REASSIGNMENT_RECONCILIATION_REQUIRED.
    /// </summary>
    Task<WhsApplyResult> ApplyOperationalWarehouseChangesAsync(
        int                              soDocEntry,
        IReadOnlyList<WarehouseLineChange> changes,
        string                           changedBy,
        CancellationToken                ct);

    /// <summary>
    /// Pre-delivery guard: verifies SoLineFragment.WhsCode == PLR.WhsCode for each
    /// fragment in the orchestration. Returns Fail when a mismatch is detected,
    /// preventing ODLN.Add() from hitting SAP rc=-10 error 1470000336.
    /// </summary>
    Task<WhsDeliveryGateResult> ValidateDeliveryWhsConsistencyAsync(
        Guid requestId,
        CancellationToken ct);

    /// <summary>
    /// Reconciles SoLineFragment/PLR/PLFR to the current SAP RDR1 warehouse when
    /// SAP is ahead of MolasIntegration (e.g. SAP update succeeded but SQL sync failed).
    /// Safe only when: State=Accepted, no physical picks (PickedQty=0), no active Delivery.
    /// Fails closed if any pick quantity exists.
    /// Returns the number of lines repaired. Returns 0 when no divergence is found.
    /// </summary>
    Task<int> ReconcileWhsStateBySoDocEntryAsync(
        int    soDocEntry,
        string reconciledBy,
        CancellationToken ct);
}
