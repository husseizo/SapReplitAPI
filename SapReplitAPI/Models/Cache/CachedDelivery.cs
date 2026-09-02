namespace SapReplitAPI.Models.Cache;

public class CachedDelivery
{
    public int    DocEntry    { get; set; }
    public int    DocNum      { get; set; }
    public DateTime DocDate   { get; set; }
    public DateTime DocDueDate{ get; set; }
    public DateTime TaxDate   { get; set; }
    public string DocStatus   { get; set; } = "";
    public string Canceled    { get; set; } = "";
    public string CardCode    { get; set; } = "";
    public string CardName    { get; set; } = "";
    public decimal DocTotal   { get; set; }
    public string DocCur      { get; set; } = "";
    public int    SlpCode     { get; set; }
    public string SlpName     { get; set; } = "";
    public int    UserSign    { get; set; }
    public string Comments    { get; set; } = "";
    public DateTime CreateDate{ get; set; }
    public int    CreateTS    { get; set; }
    public DateTime UpdateDate{ get; set; }
    public int    UpdateTS    { get; set; }
    public int    BPLId       { get; set; }
    public string? U_ReplitId       { get; set; }
    public string? ZoneRef          { get; set; }
    public string? DeliveryLocation { get; set; }

    public string DocStatusDisplay { get; set; } = "";

    public List<CachedDeliveryLine> Lines { get; set; } = new();
}
