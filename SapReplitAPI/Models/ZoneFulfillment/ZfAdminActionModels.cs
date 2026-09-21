namespace SapReplitAPI.Models.ZoneFulfillment;

// ── Action type constants ─────────────────────────────────────────────────────

public static class ZfAdminActionType
{
    public const string RefreshState       = "REFRESH_STATE";
    public const string ReplanReleased     = "REPLAN_RELEASED";
    public const string ResumeReplan       = "RESUME_REPLAN";
    public const string RetryDelivery      = "RETRY_DELIVERY";
    public const string RetryInvoice       = "RETRY_INVOICE";
    public const string ReconcileFragment  = "RECONCILE_STALE_FRAGMENT";
}

// ── Action result codes ───────────────────────────────────────────────────────

public static class ZfAdminActionError
{
    public const string OrchestrationNotFound  = "ZF_ORCHESTRATION_NOT_FOUND";
    public const string StateChanged           = "ZF_STATE_CHANGED";
    public const string PreconditionFailed     = "ZF_PRECONDITION_FAILED";
    public const string ActionNotAvailable     = "ZF_ACTION_NOT_AVAILABLE";
    public const string RecoveryRequired       = "ZF_RECOVERY_REQUIRED";
    public const string ExecutionFailed        = "ZF_EXECUTION_FAILED";
}

// ── Audit log record status ───────────────────────────────────────────────────

public static class ZfAdminAuditResult
{
    public const string Pending             = "Pending";
    public const string Success             = "Success";
    public const string Failed              = "Failed";
    public const string PreconditionFailed  = "PreconditionFailed";
    public const string Aborted             = "Aborted";
    public const string RecoveryRequired    = "RecoveryRequired";
}

// ── Audit log entry ───────────────────────────────────────────────────────────

public sealed class ZfAdminAuditEntry
{
    public long      Id               { get; set; }
    public Guid      ActionId         { get; set; } = Guid.NewGuid();
    public int       SoDocEntry       { get; set; }
    public Guid?     RequestId        { get; set; }
    public string    ActionType       { get; set; } = "";
    public string?   ReasonCode       { get; set; }
    public string    RequestedBy      { get; set; } = "";
    public DateTime  RequestedAtUtc   { get; set; }
    public DateTime? ExecutedAtUtc    { get; set; }
    public string    Result           { get; set; } = ZfAdminAuditResult.Pending;
    public string?   BeforeStateJson  { get; set; }
    public string?   AfterStateJson   { get; set; }
    public string?   EvidenceJson     { get; set; }
    public string?   ErrorCode        { get; set; }
    public string?   ErrorMessage     { get; set; }
}

// ── Request DTOs ──────────────────────────────────────────────────────────────

/// <summary>Shared fields on every admin action request.</summary>
public sealed record ZfAdminActionRequest(
    string RequestedBy,
    /// <summary>
    /// Orchestration.State from the last diagnostic read.
    /// Server re-reads live state; if it differs → 409 ZF_STATE_CHANGED.
    /// </summary>
    string ExpectedOrchestrationState
);

public sealed record ZfAdminResumeReplanRequest(
    string RequestedBy,
    string ExpectedOrchestrationState,
    Guid   OperationId
);

public sealed record ZfAdminRetryDeliveryRequest(
    string RequestedBy,
    string ExpectedOrchestrationState
);

public sealed record ZfAdminRetryInvoiceRequest(
    string RequestedBy,
    string ExpectedOrchestrationState,
    int    DeliveryDocEntry
);

public sealed record ZfAdminReconcileFragmentRequest(
    string RequestedBy,
    string ExpectedOrchestrationState
);

// ── Action result ─────────────────────────────────────────────────────────────

public sealed class ZfAdminActionResult
{
    public bool     IsSuccess    { get; init; }
    public string   ActionType   { get; init; } = "";
    public Guid     ActionId     { get; init; }
    public string?  ErrorCode    { get; init; }
    public string?  ErrorMessage { get; init; }
    public ZfOrderDiagnosticResult? BeforeState { get; init; }
    public ZfOrderDiagnosticResult? AfterState  { get; init; }
    // For mutations: informational output
    public int?     NewOdlnDocEntry  { get; init; }
    public int?     NewOinvDocEntry  { get; init; }
    public Guid?    ReplanOperationId { get; init; }
}
