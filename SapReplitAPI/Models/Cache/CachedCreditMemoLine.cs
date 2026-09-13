namespace SapReplitAPI.Models.Cache;

public class CachedCreditMemoLine
{
    public int     Id         { get; set; }
    public int     DocEntry   { get; set; }
    public int     LineNum    { get; set; }
    public string  ItemCode   { get; set; } = "";
    public string  Dscription { get; set; } = "";
    public decimal Quantity   { get; set; }
    public decimal Price      { get; set; }
    public decimal LineTotal  { get; set; }
    public string  WhsCode    { get; set; } = "";
    public int     BaseType         { get; set; }
    public int     BaseEntry        { get; set; }
    public int     BaseLine         { get; set; }
    // Resolved OINV reference (set during snapshot; NULL when SAP trace unavailable)
    public int?    InvoiceDocEntry  { get; set; }
    public int?    InvoiceLineNum   { get; set; }
}
