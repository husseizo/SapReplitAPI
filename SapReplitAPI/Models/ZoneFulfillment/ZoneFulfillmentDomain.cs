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
    public const string Closed   = "Closed";
}

/// <summary>Mirrors dbo.PickListRecord — one row per (SoLineFragment × WhsCode).</summary>
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
    public const string Pending = "Pending";
    public const string Created = "Created";
    public const string Failed  = "Failed";
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
    public required SoLineFragmentRecord       Fragment         { get; init; }
    public required PickListRecordModel        PickListRecord   { get; init; }
    public          Pkl1LineState?             SapPkl1          { get; init; }
    public          decimal                    RemainingPickedQty { get; init; }
    public          decimal                    Rdr1OpenQty      { get; init; }
    public          List<BinPickAlloc>         BinAllocations   { get; init; } = new();
    public          List<string>               ValidationErrors { get; init; } = new();
}

/// <summary>Full read-only delivery preflight result — all gate data in one object.</summary>
public sealed class DeliveryPreflightResult
{
    public required Guid                            RequestId          { get; init; }
    public required FulfillmentOrchestrationRecord  Orchestration      { get; init; }
    public required SoUdfState?                     SoUdfs             { get; init; }
    public required List<DeliveryFragmentGateData>  Fragments          { get; init; }
    public          DeliveryRecordModel?             ExistingDeliveryRecord  { get; init; }
    public          (int DocEntry, int DocNum)?      ExistingSapDelivery     { get; init; }
    public required bool                             InvoiceFilterActive { get; init; }
    public required List<string>                     GateErrors          { get; init; }
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
