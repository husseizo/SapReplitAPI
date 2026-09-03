namespace SapReplitAPI.Models.Cache;

public sealed class CachedZoneFulfillmentReportLine
{
    public int     Id                  { get; set; }
    public Guid    ReportId            { get; set; }
    public int     LineSeq             { get; set; }
    public string  ItemCode            { get; set; } = "";
    public string? Description         { get; set; }
    public decimal RequestedQty        { get; set; }
    public decimal PickedQty           { get; set; }
    public decimal DeliveredQty        { get; set; }
    public decimal UnitPrice           { get; set; }
    public decimal LineTotal           { get; set; }
    public string? WhsCode             { get; set; }
    public int?    OpklAbsEntry        { get; set; }
    public int?    PickerUserId        { get; set; }
    public string? PickerUserCode      { get; set; }
    public string? PickerName          { get; set; }
    public int?    BinAbsEntry         { get; set; }
    public string? BinCode             { get; set; }
    public decimal? BinQty             { get; set; }
    public int?    SalesOrderBaseLine  { get; set; }
    public int?    DeliveryLineNum     { get; set; }
    public int?    InvoiceLineNum      { get; set; }
    public int?    InvoiceBaseType     { get; set; }
    public int?    InvoiceBaseEntry    { get; set; }
    public int?    InvoiceBaseLine     { get; set; }
}
