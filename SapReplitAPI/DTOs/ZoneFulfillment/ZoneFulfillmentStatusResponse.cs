namespace SapReplitAPI.DTOs.ZoneFulfillment;

/// <summary>GET /api/zone-fulfillment/experimental/orders/{requestId}/status</summary>
public sealed class ZoneFulfillmentStatusResponse
{
    public Guid    RequestId        { get; init; }
    public string  State            { get; init; } = "";
    public string  DeliveryLocation { get; init; } = "";
    public int?    SoDocEntry       { get; init; }
    public int?    SoDocNum         { get; init; }
    public int     AllocationVersion { get; init; }
    public string? FailureKind      { get; init; }
    public string? ErrorMessage     { get; init; }
    public DateTime CreatedAtUtc    { get; init; }
    public DateTime UpdatedAtUtc    { get; init; }

    public List<RequestLineSummary> Lines { get; init; } = [];
}

public sealed class RequestLineSummary
{
    public Guid    RequestLineId { get; init; }
    public int     LineSeq       { get; init; }
    public string  ItemCode      { get; init; } = "";
    public decimal RequestedQty  { get; init; }
    public List<AllocationFragmentSummary> Fragments { get; init; } = [];
}

public sealed class AllocationFragmentSummary
{
    public string  WhsCode        { get; init; } = "";
    public decimal AllocatedQty   { get; init; }
    public decimal UnallocatedQty { get; init; }
    public decimal SoLineQty      { get; init; }
    public int?    SoLineNum      { get; init; }
    public int?    SourceTier     { get; init; }
}

/// <summary>POST /api/zone-fulfillment/experimental/plan (dry-run, non-reserving)</summary>
public sealed class ZonePlanResponse
{
    public Guid    RequestId              { get; init; }
    public string  DeliveryLocation       { get; init; } = "";
    public bool    HasShortage            { get; init; }
    public string  Note                   { get; init; } = "NON-RESERVING — stock may change before real order creation.";
    public string? ReceivedOriginWhsCode  { get; init; }
    public string? EffectiveOriginWhsCode { get; init; }
    public string? AllocationMode         { get; init; }
    public int?    AllocationTier         { get; init; }
    public string? AllocationReason       { get; init; }
    public List<RequestLinePlan> Lines    { get; init; } = [];
}

public sealed class RequestLinePlan
{
    public Guid    RequestLineId { get; init; }
    public int     LineSeq       { get; init; }
    public string  ItemCode      { get; init; } = "";
    public decimal RequestedQty  { get; init; }
    public List<AllocationFragmentSummary> Fragments { get; init; } = [];
}

/// <summary>201/200/202 response from POST orders endpoint.</summary>
public sealed class CreateZoneFulfillmentOrderResponse
{
    public Guid    RequestId        { get; init; }
    public string  State            { get; init; } = "";
    public int?    SoDocEntry       { get; init; }
    public int?    SoDocNum         { get; init; }
    public string  DeliveryLocation { get; init; } = "";
    public bool    HasShortage      { get; init; }
    public List<AllocationFragmentSummary> Fragments { get; init; } = [];
}
