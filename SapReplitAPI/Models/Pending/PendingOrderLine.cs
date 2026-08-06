namespace SapReplitAPI.Models.Pending;

public class PendingOrderLine
{
    public int Id { get; set; }
    public int PendingOrderId { get; set; }
    public int LineNum { get; set; }
    public string ItemCode { get; set; } = "";
    public int Quantity { get; set; }
    public decimal Price { get; set; }
    public string WhsCode { get; set; } = "001";
    public string? Dscription { get; set; }
    public string? U_Manufacturer { get; set; }

    public PendingOrder? Order { get; set; }
}
