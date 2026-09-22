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
    int  Mismatched,
    int  MissingBinInventory   // WH rows: OnHand>0 AND IsBinManaged AND bin rows = 0
);
