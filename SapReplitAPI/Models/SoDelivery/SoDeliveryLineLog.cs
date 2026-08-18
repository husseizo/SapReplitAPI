namespace SapReplitAPI.Models.SoDelivery;

/// <summary>
/// One record per Sales Order line inspected during a run.
/// LineLogStatus constants: OK | INSUFFICIENT | SKIPPED
/// </summary>
public class SoDeliveryLineLog
{
    public int      Id              { get; set; }
    public int      LogId           { get; set; }

    public int      LineNum         { get; set; }
    public string   ItemCode        { get; set; } = "";
    public string   ItemDescription { get; set; } = "";

    /// <summary>Total quantity on the SO line (from RDR1.Quantity).</summary>
    public decimal  Quantity        { get; set; }

    /// <summary>Remaining open quantity to be delivered (from RDR1.OpenQty).</summary>
    public decimal  OpenQuantity    { get; set; }

    /// <summary>Exact warehouse from the SO line — never substituted.</summary>
    public string   WarehouseCode   { get; set; } = "";

    /// <summary>Live OnHand read from OITW immediately before processing this line.</summary>
    public decimal  OnHandBefore    { get; set; }

    /// <summary>
    /// Live OnHand read from OITW after Delivery creation (SUCCESS only).
    /// Null on FAILED / INSUFFICIENT lines.
    /// </summary>
    public decimal? OnHandAfter     { get; set; }

    /// <summary>OK | INSUFFICIENT | SKIPPED</summary>
    public string   Status          { get; set; } = "";

    public string?  ErrorMessage    { get; set; }

    /// <summary>
    /// JSON array of bin allocations used for this line, e.g. [{"BinCode":"BIN-A","Qty":5.00}].
    /// Null when the warehouse is not bin-managed or when the delivery pre-dates this feature.
    /// </summary>
    public string?  BinAllocationsJson { get; set; }

    public SoDeliveryLog Log { get; set; } = null!;
}
