namespace SapReplitAPI.Models.Cache;

public sealed class CachedZoneFulfillmentReport
{
    public Guid     ReportId          { get; set; }
    public Guid     RequestId         { get; set; }
    public long     OrchestrationId   { get; set; }
    public string   ReportType        { get; set; } = "FinalFulfillment";
    public string   Status            { get; set; } = "";
    public int      SalesOrderDocEntry { get; set; }
    public int      SalesOrderDocNum  { get; set; }
    public int      DeliveryDocEntry  { get; set; }
    public int      DeliveryDocNum    { get; set; }
    public int?     InvoiceDocEntry   { get; set; }
    public int?     InvoiceDocNum     { get; set; }
    public string   CardCode          { get; set; } = "";
    public string   DeliveryLocation  { get; set; } = "";
    public string   ZoneRef           { get; set; } = "";
    public string   U_ReplitId        { get; set; } = "";
    public string?  SnapshotJson      { get; set; }
    public string?  SnapshotSha256    { get; set; }  // SHA-256 of SnapshotJson UTF-8 bytes — consistency key
    public string?  FileName          { get; set; }
    public string?  MimeType          { get; set; }
    public long?    FileSize          { get; set; }
    public string?  Sha256            { get; set; }  // PDF byte SHA-256
    public DateTime? GeneratedAtUtc   { get; set; }
    public DateTime  UpdatedAtUtc     { get; set; }
    public string?  ErrorMessage      { get; set; }
}
