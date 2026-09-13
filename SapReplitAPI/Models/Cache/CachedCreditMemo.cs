namespace SapReplitAPI.Models.Cache;

public class CachedCreditMemo
{
    public int      DocEntry   { get; set; }
    public int      DocNum     { get; set; }
    public string   CardCode   { get; set; } = "";
    public string   CardName   { get; set; } = "";
    public DateTime DocDate    { get; set; }
    public DateTime DocDueDate { get; set; }
    public string   DocStatus  { get; set; } = "";
    public string   Canceled   { get; set; } = "";
    public decimal  DocTotal   { get; set; }
    public string   Comments   { get; set; } = "";
    public int      SlpCode    { get; set; }
    public string   SlpName    { get; set; } = "";
    public string?  U_AppRef   { get; set; }
    public DateTime CreateDate { get; set; }
    public DateTime UpdateDate { get; set; }

    public List<CachedCreditMemoLine> Lines { get; set; } = new();
}
