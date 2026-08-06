namespace SapReplitAPI.Models.Invoicing;

public class OpenDeliveryDto
{
    public int DocEntry { get; set; }
    public int DocNum { get; set; }
    public string CardCode { get; set; } = "";
    public string CardName { get; set; } = "";
    public DateTime DocDate { get; set; }
    public DateTime? DocDueDate { get; set; }
    public string DocCurrency { get; set; } = "TZS";
    public decimal DocTotal { get; set; }
    public int? SlpCode { get; set; }
}

public class DeliveryLineDto
{
    public int DocEntry { get; set; }
    public int LineNum { get; set; }
    public string ItemCode { get; set; } = "";
    public decimal Quantity { get; set; }
    public decimal Price { get; set; }
}
