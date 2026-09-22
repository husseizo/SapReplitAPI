namespace SapReplitAPI.Models.Inventory;

public record BinMirrorHealthReport(
    string  Status,                // HEALTHY | MIRROR_LAGGING | MIRROR_STALE | ROW_COUNT_MISMATCH
    string  Watermark,             // BinInventory.Source watermark (last SQLite sync time)
    int     SqliteRowCount,
    int     NeonRowCount,
    int     RowCountDifference,    // SQLite - Neon (positive = Neon behind)
    double? LagSeconds             // seconds since last SQLite sync (null = no watermark)
);

/// <summary>
/// Result of GET /api/bin-inventory/spot-check/{itemCode}.
/// Always checks SAP, SQLite, Neon, and WarehouseInventory — never returns 404 before SAP lookup.
/// </summary>
public record BinSpotCheckResult(
    string   Status,           // IN_SYNC | SQLITE_MISSING | NEON_MISSING | SQLITE_AND_NEON_MISSING | BIN_TOTAL_MISMATCH | SAP_NO_BIN_STOCK | SAP_LOOKUP_FAILED
    string   ItemCode,
    int      SapBinCount,
    int      SqliteBinCount,
    int      NeonBinCount,
    decimal  WhOnHand,
    bool     IsBinManaged,
    string?  Detail,
    object?  SapRows,
    object?  SqliteRows,
    object?  NeonRows
);

/// <summary>
/// Result of POST /api/bin-inventory/repair/{itemCode}.
/// Safe: only executes if SAP read succeeds. Does NOT mutate SAP.
/// </summary>
public record BinTargetedRefreshResult(
    bool    Success,
    string  ItemCode,
    int     SapRowsRead,
    int     SqliteRowsUpserted,
    int     SqliteRowsRemoved,
    int     NeonRowsUpserted,
    int     NeonRowsRemoved,
    string? Error
);
