namespace SapReplitAPI.Models.Inventory;

public record BinSyncReport(
    int                      TotalSapRows,
    int                      DistinctItemCodes,
    IReadOnlyDictionary<string, int> RowsByWarehouse,
    int                      Upserted,
    int                      Removed,
    DateTime                 SyncTime,
    double                   DurationSeconds,
    BinReconciliationReport  Reconciliation
);

public record BinReconciliationReport(
    int  Checked,
    int  Matched,
    int  Mismatched
);
