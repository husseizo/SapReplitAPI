namespace SapReplitAPI.Models.Cache;

public class CachedReturnRequestLine
{
    public int Id { get; set; }
    public int DocEntry { get; set; }
    public int LineNum { get; set; }
    public int BaseType { get; set; }
    public int BaseEntry { get; set; }
    public int BaseLine { get; set; }
    public string ItemCode { get; set; } = "";
    public string Dscription { get; set; } = "";
    public decimal Quantity { get; set; }
    public decimal OpenQty { get; set; }
    public string WhsCode { get; set; } = "";
    public string LineStatus { get; set; } = "";
}
