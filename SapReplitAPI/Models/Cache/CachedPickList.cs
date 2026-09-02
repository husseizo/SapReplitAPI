namespace SapReplitAPI.Models.Cache;

/// <summary>
/// Cached OPKL header. Status stored raw from SAP — "Y"=Picked, "C"=Closed, "O"=Open (inferred).
/// Picker name comes from OUSR.U_NAME joined on OwnerCode; OPKL.Name is NOT authoritative.
/// </summary>
public class CachedPickList
{
    public int      AbsEntry     { get; set; }  // SAP OPKL.AbsEntry — PK
    public string   Name         { get; set; } = string.Empty;
    public int      OwnerCode    { get; set; }  // OPKL.OwnerCode = OUSR.USERID
    public string   OwnerName    { get; set; } = string.Empty;  // OUSR.U_NAME — authoritative picker display name
    public string   Status       { get; set; } = string.Empty;  // raw SAP value: "O","Y","C"
    public string   Canceled     { get; set; } = string.Empty;  // "Y"/"N"
    public string   Remarks      { get; set; } = string.Empty;
    public DateTime PickDate     { get; set; }
    public DateTime CreateDate   { get; set; }
    public DateTime UpdateDate   { get; set; }
    public string?  U_ReplitId   { get; set; }  // null for non-ZF rows; "ZF-..." for ZF rows
    public int?     SlpCode          { get; set; }  // OSLP.SlpCode via first PKL1→ORDR→OSLP
    public string   SlpName          { get; set; } = string.Empty;  // OSLP.SlpName
    public DateTime LastSyncedAt     { get; set; }
    // Single distinct value if all PKL1 lines share one SO ZoneRef; NULL if multi-SO or non-ZF.
    public string?  ZoneRef          { get; set; }  // ORDR.U_ZoneRef (aggregate, see header rule)
    public string?  DeliveryLocation { get; set; }  // ORDR.U_DeliveryLocation (aggregate, see header rule)
}
