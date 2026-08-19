namespace SapReplitAPI.Models.Inventory;

/// <summary>Raw SAP query result — ephemeral DTO, not persisted directly.</summary>
public record WarehouseInventoryRow(
    string ItemCode,
    string WhsCode,
    string WarehouseName,
    decimal OnHand,
    decimal IsCommitted,   // SAP source: OITW.IsCommited (SAP typo corrected here)
    decimal OnOrder,
    decimal AvailableToSell, // = OnHand - IsCommitted; can be negative
    bool    IsBinManaged     // OWHS.BinActivat == 'Y'
);
