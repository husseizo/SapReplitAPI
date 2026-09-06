namespace SapReplitAPI.Models.Offline;

/// <summary>
/// Workflow version discriminator — keeps V2 records from being routed to V1 processor.
/// V1 (PendingOrder) rows have no equivalent field and are never touched by V2 code.
/// </summary>
public static class FulfillmentWorkflowVersion
{
    public const string OfflineFulfillmentV2 = "OfflineFulfillmentV2";
}

/// <summary>
/// V2 offline fulfillment state machine.
/// Existing PendingOrder statuses are NOT used here; V1 records are never reinterpreted.
/// </summary>
public static class OfflineFulfillmentState
{
    // ── Capture phase ──────────────────────────────────────────
    public const string Draft               = "Draft";
    public const string PendingOffline      = "PendingOffline";

    // ── Warehouse phase ────────────────────────────────────────
    public const string OfflinePicking      = "OfflinePicking";
    public const string OfflinePickConfirmed = "OfflinePickConfirmed";

    // ── Recovery phase ─────────────────────────────────────────
    public const string WaitingForRecovery  = "WaitingForRecovery";
    public const string Recovering          = "Recovering";
    public const string SalesOrderCreated   = "SalesOrderCreated";
    public const string PickListsCreated    = "PickListsCreated";
    public const string PickReplayCompleted = "PickReplayCompleted";
    public const string DeliveryCreated     = "DeliveryCreated";
    public const string Invoiced            = "Invoiced";
    public const string Completed           = "Completed";

    // ── Terminal / blocked ─────────────────────────────────────
    public const string ReconciliationRequired = "ReconciliationRequired";
    public const string Cancelled           = "Cancelled";
    public const string Failed              = "Failed";
}

/// <summary>
/// Reconciliation reason codes — set when SAP disagrees with recorded physical truth.
/// Supervisor / IT must review before manual resolution.
/// </summary>
public static class ReconciliationReasonCode
{
    public const string SapBinShortage           = "SAP_BIN_SHORTAGE";
    public const string SapWarehouseShortage      = "SAP_WAREHOUSE_SHORTAGE";
    public const string BinNotFound              = "BIN_NOT_FOUND";
    public const string ItemInactive             = "ITEM_INACTIVE";
    public const string PickerMappingInvalid     = "PICKER_MAPPING_INVALID";
    public const string DuplicateDocumentAmbiguity = "DUPLICATE_DOCUMENT_AMBIGUITY";
    public const string CustomerInvalid          = "CUSTOMER_INVALID";
    public const string SapPreflightFailed       = "SAP_PREFLIGHT_FAILED";
}

/// <summary>
/// Recovery stage checkpoint — persisted after each SAP mutation so
/// a crash-restart can continue without duplicating documents.
/// </summary>
public static class RecoveryStage
{
    public const string None               = "None";
    public const string SalesOrderCreated  = "SalesOrderCreated";
    public const string PickListsCreated   = "PickListsCreated";
    public const string PickReplayDone     = "PickReplayDone";
    public const string DeliveryCreated    = "DeliveryCreated";
    public const string InvoiceCreated     = "InvoiceCreated";
}

/// <summary>
/// Durable record for one Offline Fulfillment V2 workflow.
/// Isolated from PendingOrder (V1) — no shared table, no shared processor.
/// </summary>
public class OfflineFulfillmentOrder
{
    public int      Id              { get; set; }

    /// <summary>Client-supplied idempotency key (Guid). Must be unique per capture request.</summary>
    public Guid     OfflineId       { get; set; } = Guid.NewGuid();

    /// <summary>Workflow discriminator — always "OfflineFulfillmentV2". Never null.</summary>
    public string   WorkflowVersion { get; set; } = FulfillmentWorkflowVersion.OfflineFulfillmentV2;

    // ── Order intent (captured at creation) ──────────────────
    public string   CardCode        { get; set; } = "";
    public DateTime DocDate         { get; set; }
    public DateTime? DeliveryDate   { get; set; }
    public int?     SlpCode         { get; set; }
    public string   DocCurrency     { get; set; } = "TZS";

    /// <summary>
    /// Preserved from capture — unlike V1 PendingOrder which loses this field.
    /// Mikocheni-side | Cluster-side | etc.
    /// </summary>
    public string   DeliveryLocation { get; set; } = "";

    // ── State machine ─────────────────────────────────────────
    public string   State           { get; set; } = OfflineFulfillmentState.Draft;

    // ── Recovery checkpointing (idempotency) ─────────────────
    /// <summary>Last completed SAP recovery stage. Never rolls back on retry.</summary>
    public string   RecoveryStage   { get; set; } = Offline.RecoveryStage.None;
    public int?     SapSalesOrderDocEntry { get; set; }
    public int?     SapSalesOrderDocNum   { get; set; }
    public int?     SapDeliveryDocEntry   { get; set; }
    public int?     SapDeliveryDocNum     { get; set; }
    public int?     SapInvoiceDocEntry    { get; set; }
    public int?     SapInvoiceDocNum      { get; set; }

    // ── Concurrency claim (cross-process safe) ────────────────
    /// <summary>Unique per claim attempt. Only one recovery worker holds the claim.</summary>
    public Guid?    RecoveryClaimId       { get; set; }
    /// <summary>UTC timestamp when recovery was claimed. Used for lease expiry (stale claim detection).</summary>
    public DateTime? RecoveryClaimedAt    { get; set; }

    // ── Reconciliation ────────────────────────────────────────
    public string?  ReconciliationReason  { get; set; }
    public string?  ErrorMessage          { get; set; }

    // ── Audit ─────────────────────────────────────────────────
    public DateTime CreatedAtUtc          { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc          { get; set; } = DateTime.UtcNow;

    // ── Navigation ────────────────────────────────────────────
    public List<OfflineFulfillmentOrderLine> Lines        { get; set; } = new();
    public List<OfflineFulfillmentPick>      Picks        { get; set; } = new();
    public List<OfflineReservation>          Reservations { get; set; } = new();
}
