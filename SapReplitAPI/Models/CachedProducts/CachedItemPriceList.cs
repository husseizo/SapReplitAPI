/// <summary>
/// Canonical cached representation of SAP ITM1 — one row per ItemCode + PriceListNum.
/// Composite primary key: (ItemCode, PriceListNum).
/// </summary>
public class CachedItemPriceList
{
    public string   ItemCode      { get; set; } = string.Empty;
    public int      PriceListNum  { get; set; }
    public decimal  Price         { get; set; }
    public string   Currency      { get; set; } = string.Empty;
    public DateTime LastUpdatedUtc { get; set; }
}
