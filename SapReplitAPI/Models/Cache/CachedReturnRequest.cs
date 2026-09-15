namespace SapReplitAPI.Models.Cache;

public class CachedReturnRequest
{
    public int DocEntry { get; set; }
    public int DocNum { get; set; }
    public string CardCode { get; set; } = "";
    public string CardName { get; set; } = "";
    public DateTime DocDate { get; set; }
    public string DocStatus { get; set; } = "";
    public string Canceled { get; set; } = "";
    public decimal DocTotal { get; set; }
    public string? U_AppRef { get; set; }
    public string? U_ReplitId { get; set; }
    public string Comments { get; set; } = "";

    public List<CachedReturnRequestLine> Lines { get; set; } = new();
}
