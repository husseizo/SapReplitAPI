namespace SapReplitAPI.Models.Cache;

/// <summary>
/// Cached PKL2 bin allocation. Natural key: (AbsEntry, Pkl2LinNum).
/// One PickListLine → many bins. BinCode/WhsCode joined from OBIN. Never collapsed.
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
    public decimal  PickQtty    { get; set; }  // numeric(18,4)
    public decimal  RelQtty     { get; set; }  // numeric(18,4)
}
