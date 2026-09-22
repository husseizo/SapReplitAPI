using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SapReplitAPI.Models;
using SapReplitAPI.Models.Inventory;
using SapReplitAPI.Services.Events;
using SapReplitAPI.Services.Inventory;
using Xunit;

namespace SapReplitAPI.Tests.BinInventoryFreshness;

/// <summary>
/// BI01–BI11: BinInventory freshness and Neon mirror correctness.
///
/// BI01–BI02: Event routing — WH-only (17/A/U/C) vs physical (15/A, 16/A, 20/A, 59/A, 60/A, 67/A/C, 13/A, 14/A)
/// BI03–BI05: RefreshTargetedBinInventoryAsync — SQLite update, SAP failure preserves cache
/// BI06:      Reconciliation MISSING_BIN_INVENTORY detection (WH stock present, no bin rows)
/// BI07–BI08: Reconciliation — matched items and sum-mismatch detection
/// BI09–BI10: BinInventorySyncService reconciliation with mixed states
/// BI11:      Physical event matrix — CanHandle returns true for all physical events
/// </summary>
public sealed class BinInventoryFreshnessTests : IDisposable
{
    private readonly SqliteConnection  _conn;
    private readonly CacheDbContext    _db;
    private readonly InventoryCacheWriteCoordinator _inventoryCoord = new();
    private readonly NeonInventoryWriteCoordinator  _neonCoord      = new();

    public BinInventoryFreshnessTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();

        var opts = new DbContextOptionsBuilder<CacheDbContext>()
            .UseSqlite(_conn)
            .Options;
        _db = new CacheDbContext(opts);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // BI01–BI02: Event handler routing
    // ─────────────────────────────────────────────────────────────────────────

    // BI01 — 17/A, 17/U, 17/C are handled exclusively by SalesOrderCommitmentEventHandler
    [Theory]
    [InlineData("17", "A")]
    [InlineData("17", "U")]
    [InlineData("17", "C")]
    public void BI01_SoEvents_HandledBySoCommitmentHandler(string objType, string txType)
    {
        var ev = new SapOutboxEvent(1, Guid.NewGuid(), objType, txType, null, null, DateTime.UtcNow, 1);

        // SalesOrderCommitmentEventHandler accepts all three SO transaction types
        var handler = new SalesOrderCommitmentEventHandler(
            null!, null!, null!, null!, null!, null!, NullLogger<SalesOrderCommitmentEventHandler>.Instance);

        Assert.True(handler.CanHandle(ev));
    }

    // BI02 — 17/A, 17/U, 17/C are NOT handled by any physical inventory handler
    [Theory]
    [InlineData("17", "A")]
    [InlineData("17", "U")]
    [InlineData("17", "C")]
    public void BI02_SoEvents_NotHandledByPhysicalHandlers(string objType, string txType)
    {
        var ev = new SapOutboxEvent(1, Guid.NewGuid(), objType, txType, null, null, DateTime.UtcNow, 1);

        // None of the physical event handlers should claim 17/x
        Assert.False(new GoodsIssueInventoryEventHandler(null!, null!, NullLogger<GoodsIssueInventoryEventHandler>.Instance).CanHandle(ev));
        Assert.False(new GoodsReceiptInventoryEventHandler(null!, null!, NullLogger<GoodsReceiptInventoryEventHandler>.Instance).CanHandle(ev));
        Assert.False(new GoodsReceiptPoEventHandler(null!, null!, NullLogger<GoodsReceiptPoEventHandler>.Instance).CanHandle(ev));
        Assert.False(new StockTransferInventoryEventHandler(null!, null!, NullLogger<StockTransferInventoryEventHandler>.Instance).CanHandle(ev));
        Assert.False(new ReturnInventoryEventHandler(null!, null!, null!, null!, NullLogger<ReturnInventoryEventHandler>.Instance).CanHandle(ev));
        Assert.False(new DeliveryInventoryEventHandler(null!, null!, null!, null!, NullLogger<DeliveryInventoryEventHandler>.Instance).CanHandle(ev));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // BI03–BI05: RefreshTargetedBinInventoryAsync
    // ─────────────────────────────────────────────────────────────────────────

    // BI03 — SAP failure in targeted bin refresh returns failure result without modifying SQLite
    [Fact]
    public async Task BI03_RefreshTargetedBin_SapFailure_ReturnsError_NoSqliteModification()
    {
        // Seed SQLite with a bin row to verify it's preserved
        _db.BinInventories.Add(new Models.Inventory.BinInventory
        {
            ItemCode    = "BM12867",
            WhsCode     = "003",
            BinAbsEntry = 662,
            BinCode     = "003-BIN-A",
            BinOnHand   = 18m,
            LastUpdated = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        var svc = new TestableRefreshService(_db, _inventoryCoord, _neonCoord,
            throwOnSapRead: true);

        var result = await svc.RefreshTargetedBinInventoryAsync(new[] { "BM12867" });

        Assert.False(result.Success);
        Assert.Contains("SAP", result.Error ?? "", StringComparison.OrdinalIgnoreCase);

        // SQLite row must be preserved — not deleted
        int remaining = await _db.BinInventories.CountAsync(b => b.ItemCode == "BM12867");
        Assert.Equal(1, remaining);
    }

    // BI04 — Successful SAP read upserts rows into SQLite
    [Fact]
    public async Task BI04_RefreshTargetedBin_SapSuccess_SqliteUpserted()
    {
        var fakeRows = new List<BinInventoryRow>
        {
            new("MB101381", "003", 662, "003-BIN-A", 18m),
        };

        var svc = new TestableRefreshService(_db, _inventoryCoord, _neonCoord,
            fakeBinRows: fakeRows);

        var result = await svc.RefreshTargetedBinInventoryAsync(new[] { "MB101381" });

        Assert.True(result.Success);
        Assert.Equal(1, result.SqliteRowsUpserted);

        int inDb = await _db.BinInventories.CountAsync(b => b.ItemCode == "MB101381");
        Assert.Equal(1, inDb);
    }

    // BI05 — Stale SQLite rows for an item are removed after fresh SAP read with zero rows
    [Fact]
    public async Task BI05_RefreshTargetedBin_SapReturnedEmpty_StaleRowsRemoved()
    {
        // Seed stale row that SAP no longer returns
        _db.BinInventories.Add(new Models.Inventory.BinInventory
        {
            ItemCode    = "STALE001",
            WhsCode     = "003",
            BinAbsEntry = 999,
            BinCode     = "003-OLD",
            BinOnHand   = 5m,
            LastUpdated = DateTime.UtcNow.AddHours(-1)
        });
        await _db.SaveChangesAsync();

        // SAP returns empty — stock went to zero
        var svc = new TestableRefreshService(_db, _inventoryCoord, _neonCoord,
            fakeBinRows: new List<BinInventoryRow>());

        var result = await svc.RefreshTargetedBinInventoryAsync(new[] { "STALE001" });

        Assert.True(result.Success);
        Assert.Equal(1, result.SqliteRowsRemoved);

        int remaining = await _db.BinInventories.CountAsync(b => b.ItemCode == "STALE001");
        Assert.Equal(0, remaining);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // BI06–BI08: BinInventorySyncService reconciliation
    // ─────────────────────────────────────────────────────────────────────────

    // BI06 — Reconciliation detects MISSING_BIN_INVENTORY when WH.OnHand>0 but no bin rows
    [Fact]
    public async Task BI06_Reconciliation_DetectsMissingBinInventory()
    {
        // Seed WH row with positive stock, bin-managed warehouse
        _db.WarehouseInventories.Add(new WarehouseInventory
        {
            ItemCode      = "BM12867",
            WhsCode       = "003",
            WarehouseName = "Warehouse 003",
            OnHand        = 18m,
            IsBinManaged  = true,
            LastUpdated   = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();
        // No BinInventory rows → this is the BM12867/MB101381 production incident pattern

        var syncSvc = new TestableBinInventorySyncService(_db);
        var report = await syncSvc.BuildReconciliationReportAsync();

        Assert.Equal(1, report.MissingBinInventory);
        Assert.Equal(0, report.Matched);
    }

    // BI07 — Reconciliation: item with matching bin total = MATCHED
    [Fact]
    public async Task BI07_Reconciliation_MatchingBinTotal_IsMatched()
    {
        _db.WarehouseInventories.Add(new WarehouseInventory
        {
            ItemCode = "ITEM001", WhsCode = "003",
            OnHand = 10m, IsBinManaged = true, LastUpdated = DateTime.UtcNow
        });
        _db.BinInventories.Add(new Models.Inventory.BinInventory
        {
            ItemCode = "ITEM001", WhsCode = "003",
            BinAbsEntry = 100, BinCode = "BIN-A", BinOnHand = 10m, LastUpdated = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        var syncSvc = new TestableBinInventorySyncService(_db);
        var report = await syncSvc.BuildReconciliationReportAsync();

        Assert.Equal(1, report.Matched);
        Assert.Equal(0, report.Mismatched);
        Assert.Equal(0, report.MissingBinInventory);
    }

    // BI08 — Reconciliation: bin total differs from WH total = MISMATCHED
    [Fact]
    public async Task BI08_Reconciliation_BinTotalMismatch_IsMismatched()
    {
        _db.WarehouseInventories.Add(new WarehouseInventory
        {
            ItemCode = "ITEM002", WhsCode = "003",
            OnHand = 15m, IsBinManaged = true, LastUpdated = DateTime.UtcNow
        });
        _db.BinInventories.Add(new Models.Inventory.BinInventory
        {
            ItemCode = "ITEM002", WhsCode = "003",
            BinAbsEntry = 101, BinCode = "BIN-B", BinOnHand = 10m, LastUpdated = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        var syncSvc = new TestableBinInventorySyncService(_db);
        var report = await syncSvc.BuildReconciliationReportAsync();

        Assert.Equal(1, report.Mismatched);
        Assert.Equal(0, report.Matched);
        Assert.Equal(0, report.MissingBinInventory);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // BI09–BI10: Mixed reconciliation states
    // ─────────────────────────────────────────────────────────────────────────

    // BI09 — Mixed: some matched, some mismatched, some missing_bin — all counted correctly
    [Fact]
    public async Task BI09_Reconciliation_MixedStates_AllClassifiedCorrectly()
    {
        // Matched: ITEM_A WH=5, Bin=5
        _db.WarehouseInventories.Add(new WarehouseInventory { ItemCode = "ITEM_A", WhsCode = "003", OnHand = 5m, IsBinManaged = true, LastUpdated = DateTime.UtcNow });
        _db.BinInventories.Add(new Models.Inventory.BinInventory { ItemCode = "ITEM_A", WhsCode = "003", BinAbsEntry = 1, BinCode = "B1", BinOnHand = 5m, LastUpdated = DateTime.UtcNow });

        // Mismatched: ITEM_B WH=10, Bin=7
        _db.WarehouseInventories.Add(new WarehouseInventory { ItemCode = "ITEM_B", WhsCode = "003", OnHand = 10m, IsBinManaged = true, LastUpdated = DateTime.UtcNow });
        _db.BinInventories.Add(new Models.Inventory.BinInventory { ItemCode = "ITEM_B", WhsCode = "003", BinAbsEntry = 2, BinCode = "B2", BinOnHand = 7m, LastUpdated = DateTime.UtcNow });

        // Missing: ITEM_C WH=18, no bin rows
        _db.WarehouseInventories.Add(new WarehouseInventory { ItemCode = "ITEM_C", WhsCode = "003", OnHand = 18m, IsBinManaged = true, LastUpdated = DateTime.UtcNow });

        await _db.SaveChangesAsync();

        var syncSvc = new TestableBinInventorySyncService(_db);
        var report = await syncSvc.BuildReconciliationReportAsync();

        Assert.Equal(1, report.Matched);
        Assert.Equal(1, report.Mismatched);
        Assert.Equal(1, report.MissingBinInventory);
        Assert.Equal(3, report.Checked);
    }

    // BI10 — WH with OnHand=0 even if IsBinManaged is skipped from reconciliation
    [Fact]
    public async Task BI10_Reconciliation_ZeroOnHand_SkippedFromMissingDetection()
    {
        // Item with zero WH stock — should NOT count as MISSING_BIN_INVENTORY
        _db.WarehouseInventories.Add(new WarehouseInventory
        {
            ItemCode = "ZERO001", WhsCode = "003",
            OnHand = 0m, IsBinManaged = true, LastUpdated = DateTime.UtcNow
        });
        // No bin rows — this is expected when stock is zero
        await _db.SaveChangesAsync();

        var syncSvc = new TestableBinInventorySyncService(_db);
        var report = await syncSvc.BuildReconciliationReportAsync();

        Assert.Equal(0, report.MissingBinInventory);
        Assert.Equal(0, report.Checked);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // BI11: Physical event matrix — CanHandle correctness
    // ─────────────────────────────────────────────────────────────────────────

    // BI11 — Physical event handlers claim correct ObjectType/TransactionType pairs
    [Theory]
    [InlineData("15", "A", typeof(DeliveryInventoryEventHandler))]
    [InlineData("16", "A", typeof(ReturnInventoryEventHandler))]
    [InlineData("20", "A", typeof(GoodsReceiptPoEventHandler))]
    [InlineData("59", "A", typeof(GoodsReceiptInventoryEventHandler))]
    [InlineData("60", "A", typeof(GoodsIssueInventoryEventHandler))]
    [InlineData("67", "A", typeof(StockTransferInventoryEventHandler))]
    [InlineData("67", "C", typeof(StockTransferInventoryEventHandler))]
    public void BI11_PhysicalEventMatrix_CorrectHandlerClaims(
        string objType, string txType, Type expectedHandlerType)
    {
        var ev = new SapOutboxEvent(1, Guid.NewGuid(), objType, txType, null, null, DateTime.UtcNow, 1);

        ISapEventHandler handler = expectedHandlerType switch
        {
            var t when t == typeof(DeliveryInventoryEventHandler)
                => new DeliveryInventoryEventHandler(null!, null!, null!, null!, NullLogger<DeliveryInventoryEventHandler>.Instance),
            var t when t == typeof(ReturnInventoryEventHandler)
                => new ReturnInventoryEventHandler(null!, null!, null!, null!, NullLogger<ReturnInventoryEventHandler>.Instance),
            var t when t == typeof(GoodsReceiptPoEventHandler)
                => new GoodsReceiptPoEventHandler(null!, null!, NullLogger<GoodsReceiptPoEventHandler>.Instance),
            var t when t == typeof(GoodsReceiptInventoryEventHandler)
                => new GoodsReceiptInventoryEventHandler(null!, null!, NullLogger<GoodsReceiptInventoryEventHandler>.Instance),
            var t when t == typeof(GoodsIssueInventoryEventHandler)
                => new GoodsIssueInventoryEventHandler(null!, null!, NullLogger<GoodsIssueInventoryEventHandler>.Instance),
            var t when t == typeof(StockTransferInventoryEventHandler)
                => new StockTransferInventoryEventHandler(null!, null!, NullLogger<StockTransferInventoryEventHandler>.Instance),
            _ => throw new InvalidOperationException($"Unknown handler type: {expectedHandlerType}")
        };

        Assert.True(handler.CanHandle(ev), $"{expectedHandlerType.Name} should handle {objType}/{txType}");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Test doubles
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Subclass of InventoryEventRefreshService that overrides the SAP bin snapshot read.
    /// Allows testing RefreshTargetedBinInventoryAsync without a live SAP COM connection or Neon DB.
    /// The Neon push is skipped by passing a null NeonDbContext — the method will catch and continue.
    /// </summary>
    private sealed class TestableRefreshService : InventoryEventRefreshService
    {
        private readonly List<BinInventoryRow> _fakeBinRows;
        private readonly bool _throwOnSapRead;

        public TestableRefreshService(
            CacheDbContext db,
            InventoryCacheWriteCoordinator inventoryCoord,
            NeonInventoryWriteCoordinator neonCoord,
            List<BinInventoryRow>? fakeBinRows = null,
            bool throwOnSapRead = false)
            : base(null!, db, null!, inventoryCoord, neonCoord,
                   NullLogger<InventoryEventRefreshService>.Instance)
        {
            _fakeBinRows   = fakeBinRows ?? new List<BinInventoryRow>();
            _throwOnSapRead = throwOnSapRead;
        }

        protected override List<BinInventoryRow> ReadBinSnapshotForItems(IReadOnlyList<string> itemCodes)
        {
            if (_throwOnSapRead)
                throw new InvalidOperationException("SAP COM unavailable in test environment");
            return _fakeBinRows
                .Where(r => itemCodes.Any(ic => ic.Equals(r.ItemCode, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }
    }

    /// <summary>
    /// Subclass of BinInventorySyncService that exposes BuildReconciliationReportAsync publicly
    /// for direct testing, using only the SQLite CacheDbContext (no SAP, no Neon).
    /// </summary>
    private sealed class TestableBinInventorySyncService : BinInventorySyncService
    {
        public TestableBinInventorySyncService(CacheDbContext db)
            : base(db, null!, new InventoryCacheWriteCoordinator(),
                   null!, new NeonInventoryWriteCoordinator(),
                   NullLogger<BinInventorySyncService>.Instance)
        { }

        // BuildReconciliationReportAsync is already public in the updated service
    }
}
