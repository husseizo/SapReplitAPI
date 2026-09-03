namespace SapReplitAPI.Models.ZoneFulfillment;

// ── Allocation-engine inputs ──────────────────────────────────────────────────

/// <summary>Warehouse in a zone with its sorted priority (1 = best).</summary>
public record ZoneWarehouse(string WhsCode, int Priority);

/// <summary>One commercial line from the client request, immutable after validation.</summary>
public record DomainRequestLine(
    Guid    RequestLineId,
    int     LineSeq,
    string  ItemCode,
    decimal RequestedQty,
    decimal UnitPrice,
    string? Description,
    string? U_ItemName,
    string? U_Manufacturer
);

/// <summary>Fresh OITW snapshot for one (ItemCode, WhsCode) pair.
/// Available is already clamped to max(0, OnHand - IsCommited).</summary>
public record OitwSnapshot(string ItemCode, string WhsCode, decimal Available);

// ── Allocation-engine output ──────────────────────────────────────────────────

/// <summary>
/// One allocation fragment: a single (RequestLineId × WhsCode) commitment.
/// SoLineQty = AllocatedQty + UnallocatedQty = exact RDR1.Quantity that will be created.
/// UnallocatedQty > 0 means this warehouse bears shortage responsibility.
/// </summary>
public record AllocationFragment(
    Guid    RequestLineId,
    string  WhsCode,
    decimal AllocatedQty,
    decimal UnallocatedQty
)
{
    public decimal SoLineQty => AllocatedQty + UnallocatedQty;
    public bool    HasShortage => UnallocatedQty > 0m;
}

/// <summary>Complete allocation result from ZoneAllocationEngine.</summary>
public sealed class AllocationResult
{
    public required IReadOnlyList<AllocationFragment> Fragments { get; init; }
    public required bool HasShortage { get; init; }

    /// <summary>Fragments for one specific RequestLine, ordered by zone priority.</summary>
    public IEnumerable<AllocationFragment> ForLine(Guid requestLineId)
        => Fragments.Where(f => f.RequestLineId == requestLineId);
}

// ── MolasIntegration persistence records ─────────────────────────────────────

public static class OrchestrationState
{
    public const string Received           = "Received";
    public const string Validating         = "Validating";
    public const string Allocating         = "Allocating";
    public const string CreatingSalesOrder = "CreatingSalesOrder";
    public const string SalesOrderCreated  = "SalesOrderCreated";
    public const string Accepted           = "Accepted";
    public const string Delivered          = "Delivered";
    public const string Canceled           = "Canceled";
    public const string Failed             = "Failed";
    public const string UnknownOutcome     = "UnknownOutcome";
}

public static class ZoneFulfillmentFailureKind
{
    public const string ValidationFailure    = "ValidationFailure";
    public const string BusinessRuleFailure  = "BusinessRuleFailure";
    public const string InsufficientStock    = "InsufficientStock";
    public const string SapDefinitiveFailure = "SapDefinitiveFailure";
    public const string SapUnknownOutcome    = "SapUnknownOutcome";
    public const string ConcurrencyConflict  = "ConcurrencyConflict";
    public const string IdempotencyConflict  = "IdempotencyConflict";
    public const string PersistenceFailure   = "PersistenceFailure";
    public const string ReconciliationRequired = "ReconciliationRequired";
    public const string ConfigurationError   = "ConfigurationError";
}

// ── Shortage detail types ─────────────────────────────────────────────────────

/// <summary>Per-item shortage detail returned when the hard-fail gate fires.</summary>
public sealed class ShortageItemDetail
{
    public required string       ItemCode           { get; init; }
    public required decimal      RequestedQty       { get; init; }
    public required decimal      AvailableQty       { get; init; }
    public decimal               ShortageQty        => RequestedQty - AvailableQty;
    public required string       DeliveryLocation   { get; init; }
    public required List<string> WarehousesExamined { get; init; }
}

/// <summary>Mirrors dbo.FulfillmentOrchestration.</summary>
public sealed class FulfillmentOrchestrationRecord
{
    public long     Id               { get; set; }
    public Guid     RequestId        { get; set; }
    public string   State            { get; set; } = OrchestrationState.Received;
    public string?  U_ReplitId       { get; set; }
    public int?     SoDocEntry       { get; set; }
    public int?     SoDocNum         { get; set; }
    public string   DeliveryLocation { get; set; } = "";
    public int      AllocationVersion { get; set; } = 1;
    public string?  FailureKind      { get; set; }
    public string?  ErrorMessage     { get; set; }
    public DateTime CreatedAtUtc     { get; set; }
    public DateTime UpdatedAtUtc     { get; set; }
}

/// <summary>Mirrors dbo.SoLineFragment.</summary>
public sealed class SoLineFragmentRecord
{
    public long   Id               { get; set; }
    public long   OrchestrationId  { get; set; }
    public Guid   RequestLineId    { get; set; }
    public long   AllocationPlanId { get; set; }
    public int    SoDocEntry       { get; set; }
    public int    SoLineNum        { get; set; }
    public string ItemCode         { get; set; } = "";
    public string WhsCode          { get; set; } = "";
    public decimal SoLineQty       { get; set; }
    public decimal AllocatedQty    { get; set; }
    public decimal UnallocatedQty  { get; set; }
    public decimal ReleasedQty     { get; set; }
    public decimal DeliveredQty    { get; set; }
}

/// <summary>Verified RDR1 line read back from SAP after ORDR.Add().</summary>
public record Rdr1Line(int LineNum, string ItemCode, string WhsCode, decimal Quantity, decimal OpenQty);

/// <summary>SAP SO creation result from ZoneFulfillmentSapOrderService.</summary>
public sealed class SapSoResult
{
    public required int    DocEntry    { get; init; }
    public required int?   DocNum      { get; init; }
    public required IReadOnlyList<Rdr1Line> Lines { get; init; }
}

// ── Pick List records ─────────────────────────────────────────────────────────

public static class PickListStatus
{
    public const string Created  = "Created";
    public const string Released = "Released";
    public const string Picked   = "Picked";
    public const string Closed   = "Closed";   // SAP OPKL.Status=C, parent ORDR cancelled or pick closed
}

/// <summary>Mirrors dbo.PickListRecord — one row per (OrchestrationId, SoLineFragment, PickListAbsEntry).
/// One fragment may have N rows over time: one per OPKL lifecycle (historical + repick).</summary>
public sealed class PickListRecordModel
{
    public long     Id               { get; set; }
    public long     OrchestrationId  { get; set; }
    public long     SoLineFragmentId { get; set; }
    public int      SoDocEntry       { get; set; }
    public int      SoLineNum        { get; set; }
    public string   WhsCode          { get; set; } = "";
    public int      PickListAbsEntry { get; set; }
    public decimal  ReleasedQty      { get; set; }
    public decimal  PickedQty        { get; set; }
    public string   Status           { get; set; } = PickListStatus.Created;
    public DateTime CreatedAtUtc     { get; set; }
    public DateTime UpdatedAtUtc     { get; set; }
}

/// <summary>SAP Pick List creation result.</summary>
public sealed class SapPickListResult
{
    public required int    AbsEntry    { get; init; }
    public required bool   IsNew       { get; init; }
}

// ── C-SAP-03 pick execution types ────────────────────────────────────────────

/// <summary>Live PKL1 line state read back from SAP — used for idempotency check and post-mutation verify.</summary>
public sealed record Pkl1LineState(
    int     AbsEntry,
    int     OrderEntry,
    int     OrderLine,
    int     BaseObject,
    decimal RelQtty,
    decimal PickQtty,
    string  PickStatus);

/// <summary>One bin allocation for a pick operation (from live OIBQ query).</summary>
public sealed record BinPickAlloc(int BinAbsEntry, string BinCode, decimal Qty);

/// <summary>OITW inventory state for one (ItemCode, WhsCode) — used for pre-mutation gate.</summary>
public sealed record OitwState(
    string  ItemCode,
    string  WhsCode,
    decimal OnHand,
    decimal IsCommited,
    decimal OnOrder);

/// <summary>ORDR UDF state — used for pre-mutation gate traceability verification.</summary>
public sealed record SoUdfState(
    string? UZoneRef,
    string? UDeliveryLocation,
    string? UReplitId,
    string  DocStatus,
    string  Canceled);

// ── Delivery records (Phase C delivery gate) ──────────────────────────────────

public static class DeliveryRecordStatus
{
    public const string Pending  = "Pending";
    public const string Created  = "Created";
    public const string Failed   = "Failed";
    public const string Canceled = "Canceled";  // SAP ODLN was subsequently canceled; record kept for audit
}

/// <summary>Mirrors dbo.DeliveryRecord — one row per ODLN creation attempt.</summary>
public sealed class DeliveryRecordModel
{
    public long     Id               { get; set; }
    public long     OrchestrationId  { get; set; }  // FK → FulfillmentOrchestration.Id
    public int?     SapDocEntry      { get; set; }
    public int?     SapDocNum        { get; set; }
    public string   ZoneRef          { get; set; } = "ZoneFulfillment";
    public string   DeliveryLocation { get; set; } = "";
    public string   CardCode         { get; set; } = "";
    public string   Status           { get; set; } = DeliveryRecordStatus.Pending;
    public string?  SapErrorMessage  { get; set; }
    public DateTime CreatedAtUtc     { get; set; }
    public DateTime UpdatedAtUtc     { get; set; }
}

/// <summary>Mirrors dbo.DeliveryFragmentRecord — one row per SO line in the delivery.</summary>
public sealed class DeliveryFragmentRecordModel
{
    public long     Id               { get; set; }
    public long     DeliveryRecordId { get; set; }
    public long     FragmentId       { get; set; }
    public int      SoDocEntry       { get; set; }
    public int      SoLineNum        { get; set; }
    public string   ItemCode         { get; set; } = "";
    public string   WhsCode          { get; set; } = "";
    public decimal  PickedQty        { get; set; }
    public decimal  DeliveredQty     { get; set; }
    public int      PickListAbsEntry { get; set; }
    public int?     DlnLineNum       { get; set; }
    public List<DeliveryFragmentBinRecordModel> Bins { get; set; } = new();
}

/// <summary>
/// Mirrors dbo.DeliveryFragmentBinRecord — one row per bin in a fragment's pick.
/// Multi-bin explicit: if a pick spans N bins, N rows are stored.
/// </summary>
public sealed class DeliveryFragmentBinRecordModel
{
    public long    Id                       { get; set; }
    public long    DeliveryFragmentRecordId { get; set; }
    public int     BinAbsEntry              { get; set; }
    public string  BinCode                  { get; set; } = "";
    public decimal Quantity                  { get; set; }
}

/// <summary>Per-fragment computed data for the delivery preflight gate.</summary>
public sealed class DeliveryFragmentGateData
{
    public required SoLineFragmentRecord       Fragment                { get; init; }
    public required PickListRecordModel        PickListRecord          { get; init; }
    public          Pkl1LineState?             SapPkl1                { get; init; }

    // ── Active vs Historical delivery truth ────────────────────────────────────
    /// <summary>Active SAP: SUM(DLN1.Qty) for non-cancelled ZF ODLNs with ZF UDFs+BaseLine. Used for eligibility.</summary>
    public          decimal                    ActiveSapDeliveredQty  { get; init; }
    /// <summary>Historical SAP: SUM(DLN1.Qty) across all ODLNs (incl. cancelled) for audit display only.</summary>
    public          decimal                    HistoricalDeliveredQty { get; init; }
    /// <summary>MolasIntegration mirror: SoLineFragment.DeliveredQty.</summary>
    public          decimal                    MolasDeliveredQty      { get; init; }

    // ── Pick list eligibility ─────────────────────────────────────────────────
    /// <summary>PKL1.PickStatus=Y → pkl1.PickQtty; else 0. A closed (C) pick cannot be reused.</summary>
    public          decimal                    EligiblePickedQty      { get; init; }
    /// <summary>EligiblePickedQty - ActiveSapDeliveredQty. Governs delivery line quantity.</summary>
    public          decimal                    RemainingDeliverableQty { get; init; }
    /// <summary>True when PKL1.PickStatus=C AND ActiveSapDeliveredQty=0 — pick closed but delivery cancelled.</summary>
    public          bool                       RequiresRepick          { get; init; }

    public          decimal                    Rdr1OpenQty            { get; init; }
    public          List<BinPickAlloc>         BinAllocations         { get; init; } = new();
    public          List<string>               ValidationErrors        { get; init; } = new();
    /// <summary>True only when RemainingDeliverableQty > 0 and ValidationErrors is empty.</summary>
    public          bool                       EligibleForDelivery    { get; init; }

    // ── Backward compat aliases ───────────────────────────────────────────────
    public decimal SapDeliveredQty    => ActiveSapDeliveredQty;
    public decimal RemainingPickedQty => RemainingDeliverableQty;
}

/// <summary>Full read-only delivery preflight result — all gate data in one object.</summary>
public sealed class DeliveryPreflightResult
{
    public required Guid                            RequestId                    { get; init; }
    public required FulfillmentOrchestrationRecord  Orchestration                { get; init; }
    public required SoUdfState?                     SoUdfs                       { get; init; }
    public required List<DeliveryFragmentGateData>  Fragments                    { get; init; }
    /// <summary>All MolasIntegration DeliveryRecords for this orchestration (0-N).</summary>
    public          List<DeliveryRecordModel>        ExistingDeliveryRecords      { get; init; } = new();
    /// <summary>ACTIVE SAP ODLN DLN1 lines: CANCELED='N', U_ZoneRef='ZoneFulfillment', proper ZF UDFs.</summary>
    public          List<SapDeliveryLine>            ExistingActiveDeliveries     { get; init; } = new();
    /// <summary>HISTORICAL SAP ODLN DLN1 lines: includes cancelled ODLNs, for audit display only.</summary>
    public          List<SapDeliveryLine>            ExistingHistoricalDeliveries { get; init; } = new();
    public required bool                             InvoiceFilterActive          { get; init; }
    public required List<string>                     GateErrors                  { get; init; }

    // ── Backward compat aliases ────────────────────────────────────────────────
    public DeliveryRecordModel?        ExistingDeliveryRecord  => ExistingDeliveryRecords.FirstOrDefault();
    public List<SapDeliveryLine>       ExistingSapDeliveries   => ExistingActiveDeliveries;
    public (int DocEntry, int DocNum)? ExistingSapDelivery     =>
        ExistingActiveDeliveries.Count > 0
            ? (ExistingActiveDeliveries[0].DocEntry, ExistingActiveDeliveries[0].DocNum)
            : null;
}

// ── Phase C2: Picker assignment ───────────────────────────────────────────────

/// <summary>
/// One row from dbo.PickerAssignment.
/// SapUserId is the OUSR.USERID integer used as OPKL.OwnerCode.
/// </summary>
public sealed class PickerAssignmentModel
{
    public int     Id         { get; set; }
    public string  WhsCode    { get; set; } = "";
    public string  UserId     { get; set; } = "";
    public string  UserName   { get; set; } = "";
    public int?    SapUserId  { get; set; }
    public bool    IsDefault  { get; set; }
    public bool    IsActive   { get; set; }
}

/// <summary>OUSR record read from SAP (read-only, no DI API needed).</summary>
public sealed class SapUserRecord
{
    public int    UserId   { get; set; }
    public string UserCode { get; set; } = "";
    public string UserName { get; set; } = "";
    public string Locked   { get; set; } = "";
}

/// <summary>Fully resolved and validated picker — safe to stamp onto OPKL.OwnerCode.</summary>
public sealed class PickerResolutionResult
{
    public required PickerAssignmentModel Assignment { get; init; }
    public required SapUserRecord         SapUser    { get; init; }
}

// ── SAP-first plural delivery truth ──────────────────────────────────────────

/// <summary>
/// One DLN1 row from a non-cancelled ZoneFulfillment ODLN.
/// Source: FindZoneFulfillmentDeliveries(uReplitId, soDocEntry).
/// BaseLine = RDR1 line index (= SoLineNum for base-type ORDR).
/// </summary>
public sealed record SapDeliveryLine(
    int     DocEntry,
    int     DocNum,
    int     DlnLineNum,
    int     BaseLine,
    string  ItemCode,
    decimal Quantity,
    string  WhsCode);

// ── ODLN readback types (Phase C mutation result) ─────────────────────────────

/// <summary>One DLN1 line read back after ODLN.Add().</summary>
public sealed class OdlnLineReadback
{
    public int     LineNum    { get; init; }
    public string  ItemCode   { get; init; } = "";
    public decimal Quantity   { get; init; }
    public string  WhsCode    { get; init; } = "";
    public int     BaseType   { get; init; }
    public int     BaseEntry  { get; init; }
    public int     BaseLine   { get; init; }
    public string? Dscription { get; init; }
}

/// <summary>Full ODLN readback — header + DLN1 lines + OIBD bin allocations.</summary>
public sealed class OdlnReadback
{
    public int                    DocEntry          { get; init; }
    public int                    DocNum            { get; init; }
    public string                 DocStatus         { get; init; } = "";
    public string                 Canceled          { get; init; } = "";
    public string                 CardCode          { get; init; } = "";
    public string?                UZoneRef          { get; init; }
    public string?                UDeliveryLocation { get; init; }
    public string?                UReplitId         { get; init; }
    public List<OdlnLineReadback> Lines             { get; init; } = new();
    public List<BinPickAlloc>     BinAllocations    { get; init; } = new();
    public long                   ElapsedMs         { get; init; }
}

/// <summary>
/// One source SO line specification for multi-line OPKL creation (Bug #2 Section 13).
/// All specs in one call must belong to the same WHS group.
/// </summary>
public sealed record PickListLineSpec(
    int    SoDocEntry,
    int    SoLineNum,
    double ReleasedQty);

/// <summary>
/// One delivery line specification for multi-line ODLN creation.
/// DurableBins come from pick list PKL2 truth, never from fresh OIBQ.
/// </summary>
public sealed record DeliveryLineSpec(
    int                          SoDocEntry,
    int                          SoLineNum,
    decimal                      Qty,
    string                       WhsCode,
    IReadOnlyList<BinPickAlloc>  DurableBins);

/// <summary>
/// Full result of ZoneFulfillmentDeliveryService.ExecuteDeliveryAsync.
/// Covers every outcome: gate errors, idempotent, mutation-disabled, and successful creation.
/// </summary>
public sealed class ZfDeliveryResult
{
    public required DeliveryPreflightResult Preflight   { get; init; }
    public          DeliveryRecordModel?    Record      { get; init; }
    public required string                  GateVerdict { get; init; }
    public          OdlnReadback?           Odln        { get; init; }
}

// ── Phase C4: Invoice records and preflight types ─────────────────────────────

public static class InvoiceRecordStatus
{
    public const string Pending = "Pending";
    public const string Created = "Created";
    public const string Failed  = "Failed";
}

/// <summary>Mirrors dbo.InvoiceRecord — one row per OINV attempt per ODLN delivery.</summary>
public sealed class InvoiceRecordModel
{
    public long     Id               { get; set; }
    public long     OrchestrationId  { get; set; }
    public long     DeliveryRecordId { get; set; }
    public int      DeliveryDocEntry { get; set; }
    public int?     SapDocEntry      { get; set; }
    public int?     SapDocNum        { get; set; }
    public string   Status           { get; set; } = InvoiceRecordStatus.Pending;
    public string?  SapErrorMessage  { get; set; }
    public DateTime CreatedAtUtc     { get; set; }
    public DateTime UpdatedAtUtc     { get; set; }
}

/// <summary>One DLN1 line with invoice-relevant fields (OpenQty > 0 gate already applied).</summary>
public record Dln1InvoiceLine(
    int     LineNum,
    int     BaseLine,
    string  ItemCode,
    string  Dscription,
    decimal Quantity,
    decimal OpenQty,
    decimal Price,
    string  Currency,
    int     BaseType,
    int     BaseEntry,
    string  WhsCode
);

/// <summary>ODLN header state for invoice preflight — includes fields not in OdlnHeaderState.</summary>
public record OdlnForInvoice(
    int      DocEntry,
    int      DocNum,
    string   DocStatus,
    string   Canceled,
    string   CardCode,
    string   DocCur,
    int      SlpCode,
    string   UZoneRef,
    string   UDeliveryLocation,
    string   UReplitId,
    DateTime DocDueDate
);

/// <summary>Existing OINV found for a delivery via SAP-first search (BaseType=15, BaseEntry=delivery).</summary>
public record SapInvoiceMatch(
    int    DocEntry,
    int    DocNum,
    string DocStatus,
    string Canceled
);

/// <summary>Full read-only invoice preflight result for C4.</summary>
public sealed class ZfInvoicePreflightResult
{
    public Guid                   RequestId             { get; set; }
    public long                   OrchestrationId       { get; set; }
    public int                    DeliveryDocEntry      { get; set; }
    public int                    DeliveryDocNum        { get; set; }
    public OdlnForInvoice?        Odln                  { get; set; }
    public List<Dln1InvoiceLine>  EligibleLines         { get; set; } = new();
    public InvoiceRecordModel?    ExistingInvoiceRecord { get; set; }
    public List<SapInvoiceMatch>  ExistingSapInvoices   { get; set; } = new();
    public List<string>           GateErrors            { get; set; } = new();
    public bool                   GatePass              => GateErrors.Count == 0;
    public bool                   MutationEnabled       { get; set; }
    public OinvCreatedReadback?   OinvCreated           { get; set; }
    public InvoiceRecordModel?    CreatedInvoiceRecord  { get; set; }
}

/// <summary>SAP OINV header + lines readback after controlled ZF OINV.Add().</summary>
public sealed class OinvCreatedReadback
{
    public int                         DocEntry         { get; init; }
    public int                         DocNum           { get; init; }
    public string                      DocStatus        { get; init; } = "";
    public string                      CardCode         { get; init; } = "";
    public string                      DocDate          { get; init; } = "";
    public string                      DocDueDate       { get; init; } = "";
    public decimal                     DocTotal         { get; init; }
    public string                      DocCurrency      { get; init; } = "";
    public string?                     UZoneRef         { get; init; }
    public string?                     UReplitId        { get; init; }
    public string?                     UDeliveryLocation { get; init; }
    public List<OinvLineReadback>      Lines            { get; init; } = new();
}

/// <summary>One INV1 line in the OINV readback.</summary>
public sealed class OinvLineReadback
{
    public int     LineNum   { get; init; }
    public string  ItemCode  { get; init; } = "";
    public decimal Quantity  { get; init; }
    public decimal Price     { get; init; }
    public int     BaseType  { get; init; }
    public int     BaseEntry { get; init; }
    public int     BaseLine  { get; init; }
}

// ── Invoice execution result (Pending-recovery gate) ─────────────────────────

/// <summary>
/// Unified result from ZoneFulfillmentInvoiceService.ExecuteInvoiceAsync.
/// Covers all state-machine paths: new OINV, idempotent, SAP-recovered, blocked.
/// </summary>
public sealed class ZfInvoiceExecuteResult
{
    // Verdict constants
    public const string V_OinvCreated          = "OINV_CREATED";
    public const string V_AlreadyCreated       = "INVOICE_ALREADY_CREATED";
    public const string V_RecoveredFromSap     = "INVOICE_RECOVERED_FROM_SAP";
    public const string V_PreflightBlocked     = "INVOICE_PREFLIGHT_BLOCKED";
    public const string V_MutationDisabled     = "MUTATION_DISABLED";

    public string                    Verdict           { get; init; } = "";
    public bool                      AlreadyApplied    { get; init; }
    public bool                      RecoveredFromSap  { get; init; }
    public long?                     InvoiceRecordId   { get; init; }
    public int?                      InvoiceDocEntry   { get; init; }
    public int?                      InvoiceDocNum     { get; init; }
    public List<string>              GateErrors        { get; init; } = new();
    public OinvCreatedReadback?      OinvReadback      { get; init; }
    public ZfInvoicePreflightResult? Preflight         { get; init; }
}

// ── Post-pick automation result ───────────────────────────────────────────────

// ── §4 Bin reservation conflict ───────────────────────────────────────────────

/// <summary>
/// Payload returned when a pre-mutation OBBQ recheck detects a bin
/// whose EffectiveAvailableQty < RequestedPickQty. BIN_RESERVATION_CONFLICT.
/// </summary>
public sealed class BinReservationConflict
{
    public required string  ItemCode              { get; init; }
    public required string  WhsCode               { get; init; }
    public required int     BinAbsEntry           { get; init; }
    public required string  BinCode               { get; init; }
    public required decimal PhysicalQty           { get; init; }
    public required decimal CommittedQty          { get; init; }
    public required decimal EffectiveAvailableQty { get; init; }
    public required decimal RequestedPickQty      { get; init; }
}

public sealed class BinReservationConflictException : Exception
{
    public BinReservationConflict Conflict { get; }
    public BinReservationConflictException(BinReservationConflict conflict)
        : base($"BIN_RESERVATION_CONFLICT: Bin={conflict.BinCode} " +
               $"Effective={conflict.EffectiveAvailableQty} Requested={conflict.RequestedPickQty}")
    {
        Conflict = conflict;
    }
}

// ── §6-§8 Cancelled order reconciliation ─────────────────────────────────────

/// <summary>One pick list reconciled against a cancelled parent ORDR.</summary>
public sealed class CancelledPickListInfo
{
    public required int     AbsEntry         { get; init; }
    public required string  SapStatus        { get; init; }
    public required decimal RelQtty          { get; init; }
    public required decimal PickQtty         { get; init; }
    public required string  SapPickStatus    { get; init; }
    public required string  MolasStatus      { get; init; }
    public required string  NewMolasStatus   { get; init; }
    public required bool    WorkflowEligible { get; init; }
}

/// <summary>
/// Item requiring physical warehouse reconciliation: picked by human but
/// parent ORDR cancelled and no ODLN exists. Stock physically out of bin.
/// </summary>
public sealed class WarehousePhysicalReconciliationItem
{
    public required int     PickListAbsEntry { get; init; }
    public required string  ItemCode         { get; init; }
    public required string  WhsCode          { get; init; }
    public required int     SoDocEntry       { get; init; }
    public required int     SoLineNum        { get; init; }
    public required decimal PickQtty         { get; init; }
    public required string  SapPickStatus    { get; init; }
    public required int     BinAbs           { get; init; }
    public required string  BinCode          { get; init; }
}

/// <summary>Full result of ReconcileCancelledOrderAsync for a RequestId.</summary>
public sealed class CancelledOrderReconciliationResult
{
    public required Guid    RequestId              { get; init; }
    public required int     SoDocEntry             { get; init; }
    public required bool    SoIsCancelled          { get; init; }
    public required string  PreviousOrchState      { get; init; }
    public required string  NewOrchState           { get; init; }
    public required bool    StateMutated           { get; init; }
    public required List<CancelledPickListInfo>               PickLists              { get; init; }
    public required List<WarehousePhysicalReconciliationItem> PhysicalPickExceptions { get; init; }
    public required string  Verdict                { get; init; }
}

/// <summary>
/// Result of evaluating delivery readiness after a Confirm Pick.
/// When AllRequiredPicksComplete=true and DeliveryTriggered=true,
/// the automatic delivery creation was attempted, followed by automatic invoice creation.
/// </summary>
public sealed class PostPickAutomationResult
{
    public bool         AllRequiredPicksComplete { get; init; }
    public bool         DeliveryTriggered        { get; init; }
    /// <summary>
    /// WaitingForOtherPicks | DeliveryCreated | DeliveryBlocked |
    /// ALL_FRAGMENTS_FULLY_DELIVERED | CRASH_RECOVERY_RECONCILED_FROM_SAP |
    /// AutomationError | OrchestrationNotFound
    /// </summary>
    public string       AutomationStatus         { get; init; } = "NotEvaluated";
    public int?         DeliveryDocEntry         { get; init; }
    public int?         DeliveryDocNum           { get; init; }
    public string?      GateVerdict              { get; init; }
    public string?      ErrorMessage             { get; init; }
    public List<string> PendingWarehouses        { get; init; } = [];
    /// <summary>Gate validation errors when AutomationStatus=DeliveryBlocked.</summary>
    public List<string> GateErrors               { get; init; } = [];

    // ── Invoice automation fields (populated only when delivery succeeded) ──

    /// <summary>
    /// OINV_CREATED | INVOICE_ALREADY_CREATED | INVOICE_RECOVERED_FROM_SAP |
    /// INVOICE_PREFLIGHT_BLOCKED | MUTATION_DISABLED | InvoiceAutomationException | null (delivery not yet successful)
    /// </summary>
    public string?      InvoiceAutomationStatus  { get; init; }
    public int?         InvoiceDocEntry          { get; init; }
    public int?         InvoiceDocNum            { get; init; }
    /// <summary>Gate errors when InvoiceAutomationStatus=INVOICE_PREFLIGHT_BLOCKED.</summary>
    public List<string> InvoiceGateErrors        { get; init; } = [];
}
