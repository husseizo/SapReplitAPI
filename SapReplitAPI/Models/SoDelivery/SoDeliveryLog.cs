namespace SapReplitAPI.Models.SoDelivery;

/// <summary>
/// One record per Sales Order processed within a run.
/// SoLogStatus constants: SUCCESS | FAILED | SKIPPED | SKIPPED_ALREADY_PROCESSED | EXCEPTION
/// </summary>
public class SoDeliveryLog
{
    public int      Id                { get; set; }
    public int      RunId             { get; set; }

    public int      SoDocEntry        { get; set; }
    public int      SoDocNum          { get; set; }
    public string   CustomerCode      { get; set; } = "";
    public string   CustomerName      { get; set; } = "";

    /// <summary>SUCCESS | FAILED | SKIPPED | SKIPPED_ALREADY_PROCESSED | EXCEPTION</summary>
    public string   Status            { get; set; } = "";

    /// <summary>DocEntry of the ODLN created. Null when no delivery was created.</summary>
    public int?     DeliveryDocEntry  { get; set; }

    /// <summary>DocNum of the ODLN created. Null when no delivery was created.</summary>
    public int?     DeliveryDocNum    { get; set; }

    /// <summary>Human-readable error summary (application-level).</summary>
    public string?  ErrorMessage      { get; set; }

    /// <summary>SAP DI-API error code from company.GetLastErrorCode(), when available.</summary>
    public string?  SapErrorCode      { get; set; }

    /// <summary>SAP DI-API error description from company.GetLastErrorDescription(), when available.</summary>
    public string?  SapErrorMessage   { get; set; }

    public DateTime ProcessedAt       { get; set; }

    /// <summary>Wall-clock time to process this SO, in milliseconds.</summary>
    public long     DurationMs        { get; set; }

    public SoDeliveryRun             Run   { get; set; } = null!;
    public ICollection<SoDeliveryLineLog> Lines { get; set; } = new List<SoDeliveryLineLog>();
}
