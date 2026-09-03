namespace SapReplitAPI.Models.ZoneFulfillment;

/// <summary>
/// Neon ZoneFulfillmentReports row (one per fulfillment cycle).
/// </summary>
public sealed class ZfReportRecord
{
    public long     Id                { get; set; }
    public Guid     ReportId          { get; set; } = Guid.NewGuid();
    public Guid     RequestId         { get; set; }
    public long     OrchestrationId   { get; set; }
    public string   ReportType        { get; set; } = "FinalFulfillment";
    public string   Status            { get; set; } = ZfReportStatus.Pending;
    public int      SalesOrderDocEntry{ get; set; }
    public int      SalesOrderDocNum  { get; set; }
    public int      DeliveryDocEntry  { get; set; }
    public int      DeliveryDocNum    { get; set; }
    public int?     InvoiceDocEntry   { get; set; }
    public int?     InvoiceDocNum     { get; set; }
    public string   CardCode          { get; set; } = "";
    public string   DeliveryLocation  { get; set; } = "";
    public string   ZoneRef           { get; set; } = "ZoneFulfillment";
    public string   U_ReplitId        { get; set; } = "";
    public string?  FileName          { get; set; }
    public string?  MimeType          { get; set; }
    public long?    FileSize          { get; set; }
    public string?  StorageProvider   { get; set; }
    public string?  StorageKey        { get; set; }
    public string?  Sha256            { get; set; }
    public string?  SnapshotJson      { get; set; }
    public DateTime? GeneratedAtUtc   { get; set; }
    public DateTime  UpdatedAtUtc     { get; set; }
    public string?  ErrorMessage      { get; set; }
}

/// <summary>
/// Neon ZoneFulfillmentReportLines row (one per order line).
/// </summary>
public sealed class ZfReportLine
{
    public long     Id                { get; set; }
    public Guid     ReportId          { get; set; }
    public int      LineSeq           { get; set; }
    public string   ItemCode          { get; set; } = "";
    public string?  Description       { get; set; }
    public decimal? RequestedQty      { get; set; }
    public decimal? PickedQty         { get; set; }
    public decimal? DeliveredQty      { get; set; }
    public decimal? UnitPrice         { get; set; }
    public decimal? LineTotal         { get; set; }
    public string?  WhsCode           { get; set; }
    public int?     OpklAbsEntry      { get; set; }
    public int?     PickerUserId      { get; set; }
    public string?  PickerUserCode    { get; set; }
    public string?  PickerName        { get; set; }
    public int?     BinAbsEntry       { get; set; }
    public string?  BinCode           { get; set; }
    public decimal? BinQty            { get; set; }
    public int?     SalesOrderBaseLine{ get; set; }
    public int?     DeliveryLineNum   { get; set; }
    public int?     InvoiceLineNum    { get; set; }
    public int?     InvoiceBaseType   { get; set; }
    public int?     InvoiceBaseEntry  { get; set; }
    public int?     InvoiceBaseLine   { get; set; }
}

/// <summary>
/// Full snapshot persisted as JSONB — all data needed for PDF regeneration without SAP access.
/// </summary>
public sealed class ZfReportSnapshot
{
    public string   ReportVersion     { get; set; } = "1";
    public string   RequestId         { get; set; } = "";
    public string   OrchestrationId   { get; set; } = "";
    public string   U_ReplitId        { get; set; } = "";
    public string   DeliveryLocation  { get; set; } = "";
    public string   ZoneRef           { get; set; } = "ZoneFulfillment";
    public string   CardCode          { get; set; } = "";
    public string?  CardName          { get; set; }
    public int      SlpCode           { get; set; }
    public string?  SlpName           { get; set; }
    public int      SoDocEntry        { get; set; }
    public int      SoDocNum          { get; set; }
    public string?  SoDocDate         { get; set; }
    public string?  SoDeliveryDate    { get; set; }
    public int      OdlnDocEntry      { get; set; }
    public int      OdlnDocNum        { get; set; }
    public string?  OdlnDocDate       { get; set; }
    public int?     OinvDocEntry      { get; set; }
    public int?     OinvDocNum        { get; set; }
    public decimal  OinvDocTotal      { get; set; }
    public string?  OinvCurrency      { get; set; }
    public string?  OinvDocDate       { get; set; }
    public List<ZfSnapshotLine>    Lines    { get; set; } = [];
    public ZfAutomationTimeline?   Timeline { get; set; }
}

public sealed class ZfSnapshotLine
{
    public int      LineSeq           { get; set; }
    public string   ItemCode          { get; set; } = "";
    public string?  Description       { get; set; }
    public decimal  RequestedQty      { get; set; }
    public decimal  PickedQty         { get; set; }
    public decimal  DeliveredQty      { get; set; }
    public decimal  UnitPrice         { get; set; }
    public decimal  LineTotal         { get; set; }
    public string?  WhsCode           { get; set; }
    public int?     OpklAbsEntry      { get; set; }
    public int?     PickerUserId      { get; set; }
    public string?  PickerUserCode    { get; set; }
    public string?  PickerName        { get; set; }
    public int?     BinAbsEntry       { get; set; }
    public string?  BinCode           { get; set; }
    public decimal? BinQty            { get; set; }
    public int?     SalesOrderBaseLine{ get; set; }
    public int?     DeliveryLineNum   { get; set; }
    public int?     InvoiceLineNum    { get; set; }
    public int?     InvoiceBaseType   { get; set; }
    public int?     InvoiceBaseEntry  { get; set; }
    public int?     InvoiceBaseLine   { get; set; }
}

public sealed class ZfAutomationTimeline
{
    public string?  OrchCreatedAt         { get; set; }
    public string?  DeliveryCreatedAt     { get; set; }
    public string?  InvoiceCreatedAt      { get; set; }
    public string?  SnapshotCapturedAt    { get; set; }
}

public static class ZfReportStatus
{
    public const string Pending       = "Pending";
    public const string SnapshotReady = "SnapshotReady";
    public const string Generated     = "Generated";
    public const string Failed        = "Failed";
}
