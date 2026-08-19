namespace SapReplitAPI.Models.Inventory;

public class BinInventory
{
    public int      Id          { get; set; }
    public string   ItemCode    { get; set; } = "";
    public string   WhsCode     { get; set; } = "";
    public int      BinAbsEntry { get; set; }
    public string   BinCode     { get; set; } = "";
    public decimal  BinOnHand   { get; set; }
    public DateTime LastUpdated { get; set; }
}
