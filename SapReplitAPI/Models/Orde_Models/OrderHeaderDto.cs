using SapReplitAPI.Models.Orde_Models;

public class OrderHeaderDto
{
   

    public int DocEntry { get; set; }
    
    public string CardCode { get; set; } = string.Empty;
    public string CardName { get; set; } = string.Empty;
    public DateTime DocDate { get; set; }
    public decimal DocTotal { get; set; }
    public string DocStatus { get; set; } = string.Empty;
    public int SlpCode { get; set; }
    public string SlpName { get; set; } = string.Empty;


    public List<OrderLineDto> Lines { get; set; } = new List<OrderLineDto>();
}