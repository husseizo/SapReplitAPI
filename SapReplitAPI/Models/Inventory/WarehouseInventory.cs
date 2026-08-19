namespace SapReplitAPI.Models.Inventory;

public class WarehouseInventory
{
    public int Id { get; set; }

    public string ItemCode { get; set; } = "";
    public string WhsCode { get; set; } = "";
    public string WarehouseName { get; set; } = "";

    public decimal OnHand { get; set; }
    public decimal IsCommitted { get; set; }   // SAP field is IsCommited (typo) — corrected here
    public decimal OnOrder { get; set; }
    public decimal AvailableToSell { get; set; } // = OnHand - IsCommitted

    public bool IsBinManaged { get; set; }     // OWHS.BinActivat == 'Y'

    public DateTime LastUpdated { get; set; }
}
