using SapReplitAPI.Models.Orde_Models;

namespace SapReplitAPI.Services.ZoneFulfillment;

/// <summary>Result of ZoneFulfillmentOrderEditCoordinator.ExecuteEditAsync / ResumeReplanAsync.</summary>
public sealed class OrderEditResult
{
    public bool    IsNotZf              { get; private init; }
    public bool    IsBlocked            { get; private init; }
    public bool    IsSuccess            { get; private init; }
    public bool    IsRecoveryRequired   { get; private init; }
    public bool    WasAmber             { get; private init; }
    public string? BlockCode            { get; private init; }
    public string? BlockReason          { get; private init; }
    public int?    BlockedLineNum        { get; private init; }
    public Guid?   ReplanOperationId    { get; private init; }
    public IReadOnlyList<int> NewPickListAbsEntries { get; private init; } = [];

    public static OrderEditResult NotZf()
        => new() { IsNotZf = true };

    public static OrderEditResult Blocked(string code, string reason, int? lineNum = null)
        => new() { IsBlocked = true, BlockCode = code, BlockReason = reason, BlockedLineNum = lineNum };

    public static OrderEditResult RecoveryRequired(string code, string reason, Guid operationId)
        => new() { IsRecoveryRequired = true, BlockCode = code, BlockReason = reason, ReplanOperationId = operationId };

    public static OrderEditResult Success(
        bool wasAmber = false, IReadOnlyList<int>? plAbsEntries = null, Guid? operationId = null)
        => new() { IsSuccess = true, WasAmber = wasAmber, NewPickListAbsEntries = plAbsEntries ?? [], ReplanOperationId = operationId };
}

/// <summary>
/// Owns the ZF Sales Order edit lifecycle for in-flight (Accepted) orders.
///
/// State machine per affected line:
///   GREEN  — no active OPKL: UpdateOrder + sync fragment + cache refresh.
///   AMBER  — Released OPKL, PKL1.PickQtty=0, PKL2.PickQtty=0: controlled replan —
///            close OPKL in SAP, retire PLR, UpdateOrder, sync fragment, new OPKL, refresh.
///   RED    — PKL1.PickQtty>0, PKL2.PickQtty>0, or active ODLN: blocked.
///
/// Durable recovery: AMBER replan is tracked in dbo.ZfReplanOperation.
/// Partial failures return IsRecoveryRequired=true with the operationId for resume.
/// Delivery automation must check for active replan before creating ODLN.
/// </summary>
public interface IZoneFulfillmentOrderEditCoordinator
{
    /// <summary>
    /// Evaluates and executes a ZF SO edit.
    /// Returns NotZf when the SO is not ZF-enrolled — caller handles normally.
    /// Returns Blocked when any gate fails — caller must return 409.
    /// Returns RecoveryRequired when a partial failure occurred — caller returns 500.
    /// Returns Success when the edit (GREEN or AMBER) completed successfully.
    /// </summary>
    Task<OrderEditResult> ExecuteEditAsync(
        int            soDocEntry,
        UpdateOrderDto dto,
        string         changedBy,
        CancellationToken ct);

    /// <summary>
    /// Idempotently resumes an AMBER replan from its last completed step.
    /// Safe to call repeatedly — will not re-cancel OPKLs already closed,
    /// duplicate UpdateOrder calls, or create duplicate Pick Lists.
    /// </summary>
    Task<OrderEditResult> ResumeReplanAsync(Guid operationId, CancellationToken ct);

    /// <summary>
    /// Reconciles a ZF SO that was edited directly in SAP GUI (17/U event path).
    /// The SO is already updated in SAP — UpdateOrder is NOT called again.
    /// Reads current RDR1, compares fragments, and if AMBER-eligible:
    ///   retires old OPKLs → advances ExternalSalesOrderAccepted → syncs fragments → creates replacement OPKLs.
    /// Returns NotZf if not enrolled or no divergence found.
    /// Returns Blocked if physical picks exist on any diverged line.
    /// Returns RecoveryRequired on partial failure.
    /// Returns Success when reconciliation completed (wasAmber=true if OPKLs were retired).
    /// </summary>
    Task<OrderEditResult> ReconcileExternalSapEditAsync(
        int    soDocEntry,
        string eventId,
        string changedBy,
        CancellationToken ct);
}
