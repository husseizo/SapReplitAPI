/// <summary>
/// Cached representation of a SAP OPLN Price List master record.
/// Primary key: PriceListNum.
/// </summary>
public class CachedPriceList
{
    public int      PriceListNum  { get; set; }
    public string   PriceListName { get; set; } = string.Empty;
    public int?     BasePriceList { get; set; }
    public decimal  Factor        { get; set; } = 1m;
    public string   Currency      { get; set; } = string.Empty;
    public bool     IsActive      { get; set; } = true;
    public DateTime LastUpdatedUtc { get; set; }
}
