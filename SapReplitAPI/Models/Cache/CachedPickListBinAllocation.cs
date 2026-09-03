namespace SapReplitAPI.Models.Cache;

/// <summary>
/// Cached PKL2 bin allocation. Natural key: (AbsEntry, PickEntry, Pkl2LinNum).
/// Pkl2LinNum resets to 0 per PickEntry in SAP, so PickEntry is required in the PK.
/// BinCode/WhsCode joined from OBIN. Never collapsed.
/// </summary>
public class CachedPickListBinAllocation
{
    public int      AbsEntry    { get; set; }  // OPKL.AbsEntry (part of PK)
    public int      Pkl2LinNum  { get; set; }  // PKL2.Pkl2LinNum (part of PK)
    public int      PickEntry   { get; set; }  // PKL2.PickEntry (FK to CachedPickListLine)
    public int      OrderEntry  { get; set; }  // from PKL1 join on (AbsEntry, PickEntry)
    public int      OrderLine   { get; set; }  // from PKL1 join on (AbsEntry, PickEntry)
    public string   ItemCode    { get; set; } = string.Empty;  // PKL2.ItemCode
    public string   WhsCode     { get; set; } = string.Empty;  // OBIN.WhsCode
    public int      BinAbsEntry { get; set; }  // PKL2.BinAbs = OBIN.AbsEntry
    public string   BinCode     { get; set; } = string.Empty;  // OBIN.BinCode
    public decimal  PickQtty      { get; set; }  // numeric(18,4)
    public decimal  RelQtty       { get; set; }  // numeric(18,4)
    public decimal  OpenCreQty    { get; set; }  // PKL2.OpenCreQty — qty not yet credited
    public string   PickListName  { get; set; } = string.Empty;  // OPKL.Name denormalized
    public string   PickListStatus{ get; set; } = string.Empty;  // OPKL.Status denormalized
    public int?     SlpCode          { get; set; }  // OSLP.SlpCode via PKL1→ORDR→OSLP
    public string   SlpName          { get; set; } = string.Empty;  // OSLP.SlpName
    public string?  ZoneRef          { get; set; }  // ORDR.U_ZoneRef via PKL1.PickEntry→OrderEntry
    public string?  DeliveryLocation { get; set; }  // ORDR.U_DeliveryLocation via PKL1.PickEntry→OrderEntry
    public string?  U_ReplitId       { get; set; }  // ORDR.U_ReplitId via PKL1.PickEntry→OrderEntry
}
