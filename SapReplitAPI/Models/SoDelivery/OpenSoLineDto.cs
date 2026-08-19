namespace SapReplitAPI.Models.SoDelivery;

/// <summary>
/// Represents one open Sales Order line returned from RDR1.
/// Only lines with LineStatus='O' and OpenQty > 0 are included.
/// Populated by SapService.GetOpenSoLines().
/// </summary>
public class OpenSoLineDto
{
    public int      DocEntry        { get; set; }
    public int      LineNum         { get; set; }
    public string   ItemCode        { get; set; } = "";
    public string   ItemDescription { get; set; } = "";

    /// <summary>Original quantity on the SO line.</summary>
    public decimal  Quantity        { get; set; }

    /// <summary>
    /// Remaining quantity not yet delivered.
    /// This is what stock validation and delivery creation use — not Quantity.
    /// </summary>
    public decimal  OpenQty         { get; set; }

    /// <summary>
    /// Warehouse as specified on the SO line.
    /// Delivery must use this exact warehouse — no substitution.
    /// </summary>
    public string   WhsCode         { get; set; } = "";
}
