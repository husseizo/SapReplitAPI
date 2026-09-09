namespace SapReplitAPI.Models.Offline;

/// <summary>
/// Original business intent captured at offline order creation.
/// Never overwritten — preserves what the customer ordered.
/// After Offline Confirm Pick, the physical truth lives in OfflineFulfillmentPick.
/// </summary>
public class OfflineFulfillmentOrderLine
{
    public int      Id                       { get; set; }
    public int      OfflineFulfillmentOrderId { get; set; }

    /// <summary>0-based sequence within the order.</summary>
    public int      LineSeq                  { get; set; }

    /// <summary>Stable per-line identifier used for SAP recovery linking.</summary>
    public Guid     RequestedLineId          { get; set; } = Guid.NewGuid();

    public string   ItemCode                 { get; set; } = "";
    public decimal  RequestedQty             { get; set; }
    public decimal  UnitPrice                { get; set; }
    public string?  Description              { get; set; }
    public string?  U_ItemName               { get; set; }
    public string?  U_Manufacturer           { get; set; }

    public OfflineFulfillmentOrder? Order    { get; set; }
}
