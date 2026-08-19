namespace SapReplitAPI.Models.Inventory;

public record BinInventoryRow(
    string  ItemCode,
    string  WhsCode,
    int     BinAbsEntry,
    string  BinCode,
    decimal BinOnHand
);
