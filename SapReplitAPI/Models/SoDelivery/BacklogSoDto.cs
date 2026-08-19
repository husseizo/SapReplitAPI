namespace SapReplitAPI.Models.SoDelivery;

/// <summary>
/// Represents an open Sales Order from a past date (DocDate &lt; today)
/// that has not yet been converted to a Delivery.
/// Returned by GET /api/so-delivery/backlog.
/// This is a visibility/reporting endpoint only — no automatic processing.
/// </summary>
public class BacklogSoDto
{
    public int      DocEntry          { get; set; }
    public int      DocNum            { get; set; }
    public DateTime DocDate           { get; set; }
    public string   CustomerCode      { get; set; } = "";
    public string   CustomerName      { get; set; } = "";
    public decimal  DocTotal          { get; set; }

    /// <summary>Number of SO lines that still have OpenQty > 0.</summary>
    public int      OpenLinesCount    { get; set; }

    /// <summary>Calendar days since DocDate.</summary>
    public int      DaysOpen          { get; set; }

    // ── Enrichment from local SQLite SoDeliveryLogs ──────────────────────

    /// <summary>Date of the most recent processing attempt for this SO. Null if never attempted.</summary>
    public DateTime? LastAttemptDate  { get; set; }

    /// <summary>Status from the most recent attempt (e.g. FAILED, EXCEPTION). Null if never attempted.</summary>
    public string?  LastAttemptStatus { get; set; }

    /// <summary>ErrorMessage from the most recent failed attempt. Null if never attempted or last attempt was SUCCESS.</summary>
    public string?  LastFailureReason { get; set; }
}
