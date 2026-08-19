namespace SapReplitAPI.Models.Inventory;

public record ItemWarehouseInventoryResponse(
    string ItemCode,
    IReadOnlyList<WarehouseInventoryDto> Warehouses
);
