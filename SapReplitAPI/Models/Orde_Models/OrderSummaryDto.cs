using SapReplitAPI.Models.Orde_Models;

public class OrderSummaryDto
{
    public int DocEntry { get; set; }
    public int DocNum { get; set; }
    public DateTime DocDate { get; set; }
    public string CustomerName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public decimal OrderValue { get; set; }
    public List<OrderLineDto> Lines { get; set; } = new List<OrderLineDto>();

    public int SlpCode { get; set; }
    public string SlpName { get; set; } = string.Empty;
}