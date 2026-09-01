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
    public DateTime LastSyncedAt { get; set; }
}
