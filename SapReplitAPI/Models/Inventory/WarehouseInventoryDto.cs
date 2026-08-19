namespace SapReplitAPI.Models.Inventory;

public record WarehouseInventoryDto(
    string  WhsCode,
    string  WarehouseName,
    decimal OnHand,
    decimal IsCommitted,
    decimal AvailableToSell,
    decimal OnOrder,
    bool    IsBinManaged,
    DateTime LastUpdated
);
