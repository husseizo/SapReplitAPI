namespace SapReplitAPI.Models.Cache;

/// <summary>
/// Cached PKL1 line. Natural key: (AbsEntry, PickEntry).
/// PickStatus stored raw from SAP — "Y"=Picked, "C"=Closed. SEPARATE enum from OPKL.Status.
/// ItemCode, Dscription, WhsCode joined from RDR1. SourceSoDocNum joined from ORDR.
/// </summary>
public class CachedPickListLine
{
    public int      AbsEntry       { get; set; }  // OPKL.AbsEntry (FK + part of PK)
    public int      PickEntry      { get; set; }  // PKL1.PickEntry (part of PK)
    public int      OrderEntry     { get; set; }  // PKL1.OrderEntry = ORDR.DocEntry
    public int      OrderLine      { get; set; }  // PKL1.OrderLine  = RDR1.LineNum
    public int      BaseObject     { get; set; }  // 17 = Sales Order
    public decimal  RelQtty        { get; set; }  // numeric(18,4)
    public decimal  PickQtty       { get; set; }  // numeric(18,4)
    public string   PickStatus     { get; set; } = string.Empty;  // raw: "Y" or "C"
    public decimal  PrevReleas     { get; set; }  // numeric(18,4)
    public string   ItemCode       { get; set; } = string.Empty;  // from RDR1
    public string   Dscription     { get; set; } = string.Empty;  // from RDR1
    public string   WhsCode        { get; set; } = string.Empty;  // from RDR1
    public int?     SourceSoDocNum { get; set; }  // from ORDR.DocNum — nullable (join may find nothing)
}
