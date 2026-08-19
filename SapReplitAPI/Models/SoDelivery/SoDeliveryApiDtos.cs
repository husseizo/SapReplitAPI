namespace SapReplitAPI.Models.SoDelivery;

// ── Inbound ───────────────────────────────────────────────────────────────────

/// <summary>
/// Pilot single-order process request. CardCode safety guard prevents processing the
/// wrong customer's order if DocEntry resolves to an unexpected customer.
/// </summary>
public class PilotOrderRequest
{
    /// <summary>
    /// Expected SAP CardCode of the target SO. If provided and the resolved SO
    /// has a different CardCode, the pilot is aborted with 400 before any processing.
    /// Strongly recommended — obtain CardCode from GET /api/so-delivery/pilot/customer-orders first.
    /// </summary>
    public string? ExpectedCardCode { get; set; }

    /// <summary>
    /// Set true to bypass two guards that would otherwise block re-processing:
    ///   1. COMPLETED run guard for today's date (creates a new run instead of rejecting)
    ///   2. LOCAL_SUCCESS_BUT_SAP_STILL_OPEN history check (re-attempts delivery even if a prior
    ///      SUCCESS log exists for this SO — use only after confirming the prior ODLN was cancelled in SAP).
    /// SAP remains the final duplicate guard — delivery.Add() will fail if a live ODLN already exists.
    /// </summary>
    public bool Force { get; set; }
}

public class SoDeliveryRunRequest
{
    /// <summary>
    /// Business processing date (EAT). Omit to default to today's EAT date.
    /// Must equal today's EAT date — historical dates are rejected (they would
    /// backdate the ODLN DocDate in SAP). Future dates are also rejected.
    /// </summary>
    public DateTime? Date  { get; set; }

    /// <summary>Override an existing COMPLETED or RUNNING run guard for the same date.</summary>
    public bool Force { get; set; }
}

// ── Outbound ──────────────────────────────────────────────────────────────────

/// <summary>Flat run summary. Returned by POST /run and used as base for detail responses.</summary>
public class SoDeliveryRunResponse
{
    public int       RunId                  { get; set; }
    public string    ProcessingDate         { get; set; } = "";
    public string    Status                 { get; set; } = "";
    public string    TriggeredBy            { get; set; } = "";
    public bool      IsForced               { get; set; }
    public int       TotalOrders            { get; set; }
    public int       SuccessCount           { get; set; }
    public int       FailedCount            { get; set; }
    public int       SkippedCount           { get; set; }
    public int       ExceptionCount         { get; set; }
    public int       TotalDeliveriesCreated { get; set; }
    public DateTime  StartTime              { get; set; }
    public DateTime? EndTime                { get; set; }
    public string?   ErrorMessage           { get; set; }
    public string?   PdfPath                { get; set; }
}

/// <summary>Run with SO-level logs (no line logs). Returned by GET /runs/latest.</summary>
public class SoDeliveryRunWithLogsResponse : SoDeliveryRunResponse
{
    public List<SoDeliveryLogDto> Logs { get; set; } = new();
}

/// <summary>Run with SO-level logs AND line logs. Returned by GET /runs/{runId}.</summary>
public class SoDeliveryRunDetailResponse : SoDeliveryRunResponse
{
    public List<SoDeliveryLogDetailDto> Logs { get; set; } = new();
}

/// <summary>SO-level log summary. Does not include line logs.</summary>
public class SoDeliveryLogDto
{
    public int      Id               { get; set; }
    public int      SoDocEntry       { get; set; }
    public int      SoDocNum         { get; set; }
    public string   CustomerCode     { get; set; } = "";
    public string   CustomerName     { get; set; } = "";
    public string   Status           { get; set; } = "";
    public int?     DeliveryDocEntry { get; set; }
    public int?     DeliveryDocNum   { get; set; }
    public string?  ErrorMessage     { get; set; }
    public string?  SapErrorCode     { get; set; }
    public string?  SapErrorMessage  { get; set; }
    public DateTime ProcessedAt      { get; set; }
    public long     DurationMs       { get; set; }
}

/// <summary>SO-level log with line-level details. Used in /runs/{runId} and /failed.</summary>
public class SoDeliveryLogDetailDto : SoDeliveryLogDto
{
    public List<SoDeliveryLineLogDto> Lines { get; set; } = new();
}

/// <summary>Line-level inventory audit log.</summary>
public class SoDeliveryLineLogDto
{
    public int      LineNum         { get; set; }
    public string   ItemCode        { get; set; } = "";
    public string   ItemDescription { get; set; } = "";
    public decimal  Quantity        { get; set; }
    public decimal  OpenQuantity    { get; set; }
    public string   WarehouseCode   { get; set; } = "";
    public decimal  OnHandBefore    { get; set; }
    public decimal? OnHandAfter     { get; set; }
    public string   Status          { get; set; } = "";
    public string?  ErrorMessage    { get; set; }
}

/// <summary>Generic paginated result wrapper.</summary>
public class PagedResult<T>
{
    public List<T> Items      { get; set; } = new();
    public int     Page       { get; set; }
    public int     PageSize   { get; set; }
    public int     TotalCount { get; set; }
    public int     TotalPages { get; set; }
}
