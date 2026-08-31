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
    public const string SapDefinitiveFailure = "SapDefinitiveFailure";
    public const string SapUnknownOutcome    = "SapUnknownOutcome";
    public const string ConcurrencyConflict  = "ConcurrencyConflict";
    public const string IdempotencyConflict  = "IdempotencyConflict";
    public const string PersistenceFailure   = "PersistenceFailure";
    public const string ReconciliationRequired = "ReconciliationRequired";
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
