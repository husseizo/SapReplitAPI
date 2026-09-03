namespace SapReplitAPI.Services.ZoneFulfillment;

/// <summary>
/// Singleton that records the result of the startup schema probe for dbo.InvoiceRecord.
/// If InvoiceRecordAvailable is false, automatic invoice creation is suppressed for the
/// lifetime of this service instance, regardless of InvoiceAutomationEnabled config.
/// </summary>
public sealed class ZoneFulfillmentInvoiceStartupHealth
{
    /// <summary>True when the startup probe confirmed dbo.InvoiceRecord is queryable.</summary>
    public bool InvoiceRecordAvailable { get; set; } = true;

    /// <summary>Human-readable result of the startup probe, shown in diagnostics.</summary>
    public string ProbeMessage { get; set; } = "Not yet probed.";
}
