namespace SapReplitAPI.Models.Cache;

public class CachedDeliveryLine
{
    public int     DocEntry    { get; set; }
    public int     LineNum     { get; set; }
    public string  ItemCode    { get; set; } = "";
    public string  Dscription  { get; set; } = "";
    public decimal Quantity    { get; set; }
    public decimal OpenQty     { get; set; }
    public string  WhsCode     { get; set; } = "";
    public decimal Price       { get; set; }
    public decimal LineTotal   { get; set; }
    public string  Currency    { get; set; } = "";
    public int     BaseType    { get; set; }
    public int     BaseEntry   { get; set; }
    public int     BaseLine    { get; set; }
    public int     TargetType  { get; set; }
    public int     TrgetEntry  { get; set; }
}
