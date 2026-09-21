namespace SapReplitAPI.Models.ZoneFulfillment;

// ── Consistency status codes ─────────────────────────────────────────────────

public static class ZfConsistencyStatus
{
    public const string Healthy                     = "ZF_HEALTHY";
    public const string Waiting                     = "ZF_WAITING";
    public const string AwaitingPick                = "ZF_AWAITING_PICK";
    public const string AwaitingBinSelection        = "ZF_AWAITING_BIN_SELECTION";
    public const string PhysicalPickStarted         = "ZF_PHYSICAL_PICK_STARTED";
    public const string ReplanAvailable             = "ZF_REPLAN_AVAILABLE";
    public const string StaleFragmentAfterValidPick = "ZF_STALE_FRAGMENT_WHS_AFTER_VALID_PICK";
    public const string FragWhsMismatch             = "ZF_FRAG_WHS_MISMATCH";
    public const string ReplanInProgress            = "ZF_ORDER_REPLAN_IN_PROGRESS";
    public const string ReplanRecoveryRequired      = "ZF_ORDER_REPLAN_RECOVERY_REQUIRED";
    public const string DeliveryFailedRetry         = "ZF_DELIVERY_FAILED_RETRY";
    public const string OrderStateMismatch          = "ZF_ORDER_STATE_MISMATCH";
    public const string Delivered                   = "ZF_DELIVERED";
    public const string InvoicePending              = "ZF_INVOICE_PENDING";
    public const string Completed                   = "ZF_COMPLETED";
    public const string Canceled                    = "ZF_CANCELED";
    public const string Failed                      = "ZF_FAILED";
    public const string Unknown                     = "ZF_UNKNOWN";
}

// ── Available action descriptor (Phase 1A = informational only) ──────────────

public sealed record ZfAvailableAction(
    string Code,
    bool   Enabled,
    bool   MutationAvailable,
    string Reason
);

// ── Classifier input ─────────────────────────────────────────────────────────

/// <summary>
/// Pure value input to ZfConsistencyClassifier.Classify().
/// Built by ZfAdminDiagnosticService from live SAP + MolasIntegration reads.
/// </summary>
public sealed record ZfClassifierInput(
    string   OrchState,
    string?  FailureKind,
    ZfReplanDiagnostic?                  ActiveReplan,
    IReadOnlyList<ZfFragmentClassInput>  Fragments,
    IReadOnlyList<ZfDeliveryClassInput>  Deliveries,
    bool                                 HasSuccessfulInvoice
);

/// <summary>Per-fragment input to the classifier.</summary>
public sealed record ZfFragmentClassInput(
    int     SoLineNum,
    string  FragmentWhsCode,
    // Live SAP reads
    string? SapRdr1WhsCode,
    // Active pick list state
    string? PickListRecordWhsCode,
    decimal SapPkl1PickQtty,
    string? SapPkl1PickStatus,
    // Physical bin warehouse (from OBIN join on PKL2.BinAbs)
    string? SapPkl2BinWhsCode,
    // Delivery gate
    bool    HasActiveOdln
);

/// <summary>Per-delivery input to the classifier.</summary>
public sealed record ZfDeliveryClassInput(
    string Status   // DeliveryRecordStatus constants
);

// ── Consistency verdict ──────────────────────────────────────────────────────

public sealed record ZfConsistencyVerdict(
    string  Status,
    string? Detail,
    bool    HasFragmentMismatch,
    bool    HasActiveReplan,
    bool    HasDeliveryFailure,
    bool    InvoicePending
);

// ── Fragment diagnostic (for response) ──────────────────────────────────────

public sealed record ZfFragmentDiagnostic(
    long     Id,
    int      SoLineNum,
    string   ItemCode,
    string   FragmentWhsCode,
    decimal  SoLineQty,
    decimal  AllocatedQty,
    decimal  DeliveredQty,
    string?  OriginalWhsCode,
    DateTime? WhsChangedAtUtc,
    string?  WhsChangedBy,
    // Live SAP: RDR1
    string?  SapRdr1WhsCode,
    string?  SapRdr1LineStatus,
    decimal  SapRdr1OpenQty,
    bool     WhsMatchesSap,
    // Active pick list record (most recent)
    string?  PickListWhsCode,
    int?     PickListAbsEntry,
    decimal  PickListPickedQty,
    string   PickListStatus,
    // Live SAP: PKL1
    decimal  SapPkl1PickQtty,
    string?  SapPkl1PickStatus,
    // Live SAP: PKL2/OBIN bin
    string?  SapPkl2BinCode,
    string?  SapPkl2BinWhsCode
);

// ── Delivery diagnostic ──────────────────────────────────────────────────────

public sealed record ZfDeliveryDiagnostic(
    long     Id,
    string   Status,
    int?     SapDocEntry,
    int?     SapDocNum,
    string?  SapErrorMessage,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    // Live SAP ODLN state (populated for Status=Created)
    string?  OdlnDocStatus,
    string?  OdlnCanceled,
    string?  OdlnCardCode
);

// ── Replan diagnostic ────────────────────────────────────────────────────────

public sealed record ZfReplanDiagnostic(
    long      Id,
    Guid      OperationId,
    string    CurrentStep,
    string?   LastGoodStep,
    string?   LastError,
    string    ChangedBy,
    DateTime  StartedAtUtc,
    DateTime? CompletedAtUtc
);

// ── Invoice diagnostic ───────────────────────────────────────────────────────

public sealed record ZfInvoiceDiagnostic(
    long     Id,
    string   Status,
    int      DeliveryDocEntry,
    int?     SapDocEntry,
    int?     SapDocNum,
    string?  SapErrorMessage,
    DateTime CreatedAtUtc
);

// ── Main aggregate response ──────────────────────────────────────────────────

public sealed class ZfOrderDiagnosticResult
{
    public long     OrchestrationId  { get; init; }
    public Guid     RequestId        { get; init; }
    public int      SoDocEntry       { get; init; }
    public int?     SoDocNum         { get; init; }
    public string   State            { get; init; } = "";
    public string   DeliveryLocation { get; init; } = "";
    public string?  FailureKind      { get; init; }
    public string?  ErrorMessage     { get; init; }
    public string?  UZoneRef         { get; init; }
    public string?  UReplitId        { get; init; }
    public string?  OriginWhsCode    { get; init; }
    public string?  AllocationReason { get; init; }
    public DateTime CreatedAtUtc     { get; init; }
    public DateTime UpdatedAtUtc     { get; init; }

    public IReadOnlyList<ZfFragmentDiagnostic>  Fragments        { get; init; } = [];
    public IReadOnlyList<ZfDeliveryDiagnostic>  Deliveries       { get; init; } = [];
    public IReadOnlyList<ZfInvoiceDiagnostic>   Invoices         { get; init; } = [];
    public ZfReplanDiagnostic?                  ActiveReplan     { get; init; }
    public ZfConsistencyVerdict                 Consistency      { get; init; } = default!;
    public IReadOnlyList<ZfAvailableAction>     AvailableActions { get; init; } = [];
}

// ── Timeline ─────────────────────────────────────────────────────────────────

public sealed record ZfTimelineEntry(
    DateTime TimestampUtc,
    string   EventType,
    string   Description,
    string?  Detail
);

public sealed class ZfOrderTimelineResult
{
    public int                            SoDocEntry { get; init; }
    public Guid                           RequestId  { get; init; }
    public IReadOnlyList<ZfTimelineEntry> Events     { get; init; } = [];
}

// ── Incidents ─────────────────────────────────────────────────────────────────

public sealed class ZfOrderIncidentResult
{
    public int                                 SoDocEntry         { get; init; }
    public Guid                                RequestId          { get; init; }
    public IReadOnlyList<ZfDeliveryDiagnostic> FailedDeliveries   { get; init; } = [];
    public IReadOnlyList<ZfReplanDiagnostic>   ReplanConflicts    { get; init; } = [];
    public IReadOnlyList<string>               ConsistencyIssues  { get; init; } = [];
}
