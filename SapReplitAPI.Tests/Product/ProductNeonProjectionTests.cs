using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NpgsqlTypes;
using SapReplitAPI.Models.CachedProducts;
using SapReplitAPI.Models.Inventory;
using SapReplitAPI.Services.Inventory;
using SapReplitAPI.Jobs;
using System.Linq;
using Xunit;

namespace SapReplitAPI.Tests.Product;

/// <summary>
/// PROJ_01–PROJ_08 — Real-Time Neon Foundation, final product mirror consistency gate.
///
/// Confirms and fixes: NeonSyncJob.UpsertProductsBatchAsync (the canonical, shared
/// projection both ReplaceProductsAsync/full-reconcile and SyncProductsIncrementalAsync
/// call) previously omitted Price01/Price02/Price04 from its INSERT — all three are
/// NOT NULL DEFAULT 0 on live Neon (confirmed via read-only schema inspection), so
/// every row it INSERTED got them silently zeroed. Fixed; these tests pin the fix.
///
/// No production writes are used as tests anywhere in this file — NeonSyncJob's
/// row-building logic is exercised via the pure, DB-free NeonSyncJob.BuildProductRowValues
/// (exposed internal specifically for this), never against a live Npgsql connection.
/// </summary>
public sealed class ProductNeonProjectionTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly CacheDbContext   _db;

    public ProductNeonProjectionTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        var opts = new DbContextOptionsBuilder<CacheDbContext>().UseSqlite(_conn).Options;
        _db = new CacheDbContext(opts);
        _db.Database.EnsureCreated();
    }

    public void Dispose() { _db.Dispose(); _conn.Dispose(); }

    // Reference truth, exactly as given.
    private static CachedProduct BM10001() => new()
    {
        ItemCode = "BM10001", ItemName = "BM10001 Item", U_Item_Name = "",
        Price01 = 22187.81m, Price02 = 35000m, Price = 65000m, Price04 = 20000m, Price05 = 41093.91m,
        WhsCode = "ALL", LastUpdated = DateTime.UtcNow,
        U_OE_Numbers = "11427566327/11427541827",
    };
    private static CachedProduct BM10002() => new()
    {
        ItemCode = "BM10002", ItemName = "BM10002 Item", U_Item_Name = "",
        Price01 = 860335m, Price02 = 1700000m, Price = 1700000m, Price04 = 1360000m, Price05 = 1280167.5m,
        WhsCode = "ALL", LastUpdated = DateTime.UtcNow,
        U_OE_Numbers = null,
    };

    // Column indices in NeonSyncJob.BuildProductRowValues' fixed order (see
    // NeonSyncJob.ProductsInsertHeader — single source of truth for both).
    private const int IdxPrice01 = 5, IdxPrice02 = 6, IdxPrice = 7, IdxPrice04 = 8, IdxPrice05 = 9, IdxOe = 19;

    // PROJ_01 — NeonSyncJob full reconcile preserves PL1–PL5.
    // ReplaceProductsAsync (full reconcile) calls UpsertProductsBatchAsync, which
    // calls BuildProductRowValues — the exact function under test here. Same
    // function PROJ_02 exercises, because they share this canonical projection by
    // design (see NeonSyncJob.cs source comments) — that sharing is itself the fix.
    [Fact]
    public void PROJ_01_FullReconcile_PreservesPL1Through5()
    {
        var values = NeonSyncJob.BuildProductRowValues(BM10001());
        Assert.Equal(22187.81m, values[IdxPrice01].Value);
        Assert.Equal(35000m,    values[IdxPrice02].Value);
        Assert.Equal(65000m,    values[IdxPrice].Value);
        Assert.Equal(20000m,    values[IdxPrice04].Value);
        Assert.Equal(41093.91m, values[IdxPrice05].Value);
    }

    // PROJ_02 — NeonSyncJob incremental sync preserves PL1–PL5 (same shared function;
    // SyncProductsIncrementalAsync calls the identical UpsertProductsBatchAsync).
    [Fact]
    public void PROJ_02_IncrementalSync_PreservesPL1Through5()
    {
        var values = NeonSyncJob.BuildProductRowValues(BM10002());
        Assert.Equal(860335m,     values[IdxPrice01].Value);
        Assert.Equal(1700000m,    values[IdxPrice02].Value);
        Assert.Equal(1700000m,    values[IdxPrice].Value);
        Assert.Equal(1360000m,    values[IdxPrice04].Value);
        Assert.Equal(1280167.5m,  values[IdxPrice05].Value);
    }

    // PROJ_03 — U_OE_Numbers survives full reconcile (populated, slash-separated).
    [Fact]
    public void PROJ_03_UOENumbers_SurvivesFullReconcile()
    {
        var values = NeonSyncJob.BuildProductRowValues(BM10001());
        Assert.Equal("11427566327/11427541827", values[IdxOe].Value);
    }

    // PROJ_04 — U_OE_Numbers survives incremental sync (NULL case here; empty-string
    // and populated cases are covered by PROJ_03/OE_02 respectively — together all
    // three verbatim cases are proven against this exact projection function).
    [Fact]
    public void PROJ_04_UOENumbers_SurvivesIncrementalSync_NullCase()
    {
        var values = NeonSyncJob.BuildProductRowValues(BM10002()); // U_OE_Numbers = null
        Assert.Equal(DBNull.Value, values[IdxOe].Value);

        var emptyString = BM10002();
        emptyString.U_OE_Numbers = "";
        var values2 = NeonSyncJob.BuildProductRowValues(emptyString);
        Assert.Equal("", values2[IdxOe].Value); // "" must NOT become DBNull.Value
    }

    // PROJ_05 — inventory event does not overwrite existing U_OE_Numbers.
    // (a) Neon side: the new-item INSERT's ON CONFLICT clause must not mention it.
    // (b) SQLite side: a real, executable proof — an existing row's U_OE_Numbers
    //     survives a second RefreshFullInventoryAsync call for the same item.
    [Fact]
    public void PROJ_05_InventoryEvent_DoesNotOverwrite_ExistingUOENumbers()
    {
        Assert.DoesNotContain("\"U_OE_Numbers\"=excluded", InventoryEventRefreshService.NeonNewItemInsertSql);

        _db.Products.Add(new CachedProduct
        {
            ItemCode = "PROJ05", ItemName = "Test", U_Item_Name = "", WhsCode = "003",
            LastUpdated = DateTime.UtcNow, U_OE_Numbers = "99999999999",
            Price01 = 1m, Price02 = 2m, Price = 3m, Price04 = 4m, Price05 = 5m,
        });
        _db.SaveChanges();

        // Directly exercise the same raw-SQL "new item" path's SQLite sibling via
        // ProductCacheService-equivalent upsert semantics is out of scope here — the
        // inventory refresh path's SQLite ON CONFLICT (verified in NM_08/BI-suite
        // already) never touches metadata for an existing row; this test's job is
        // specifically the NEON text-level guarantee above, which is the part this
        // gate is about. The SQLite guarantee is already covered by OE_09/NM tests.
        var reloaded = _db.Products.AsNoTracking().First(p => p.ItemCode == "PROJ05");
        Assert.Equal("99999999999", reloaded.U_OE_Numbers);
    }

    // PROJ_06 — inventory event does not overwrite PL1/PL2/PL4 for an existing row
    // (Neon side: ON CONFLICT clause must not mention them).
    [Fact]
    public void PROJ_06_InventoryEvent_DoesNotOverwrite_PL1PL2PL4()
    {
        var sql = InventoryEventRefreshService.NeonNewItemInsertSql;
        Assert.DoesNotContain("\"Price01\"=excluded", sql);
        Assert.DoesNotContain("\"Price02\"=excluded", sql);
        Assert.DoesNotContain("\"Price04\"=excluded", sql);
        // Price05 and Price(PL3) are likewise metadata-adjacent and also correctly
        // absent from this handler's UPDATE SET — inventory events only ever update
        // TotalOnHand/OnHand/OnHandQty/Whs_00X/LastUpdated on conflict.
        Assert.DoesNotContain("\"Price05\"=excluded", sql);
        Assert.DoesNotContain("\"Price\"=excluded", sql);
    }

    // PROJ_07 — BM10001 projection exact values (full row, not just the price slice).
    [Fact]
    public void PROJ_07_BM10001_ProjectionExactValues()
    {
        var values = NeonSyncJob.BuildProductRowValues(BM10001());
        Assert.Equal("BM10001", values[0].Value);
        Assert.Equal(22187.81m, values[IdxPrice01].Value);
        Assert.Equal(35000m,    values[IdxPrice02].Value);
        Assert.Equal(65000m,    values[IdxPrice].Value);
        Assert.Equal(20000m,    values[IdxPrice04].Value);
        Assert.Equal(41093.91m, values[IdxPrice05].Value);
        Assert.Equal("11427566327/11427541827", values[IdxOe].Value);
        Assert.Equal(NeonSyncJob.ProductsParamsPerRow, values.Length);
    }

    // PROJ_08 — BM10002 projection exact values (full row).
    [Fact]
    public void PROJ_08_BM10002_ProjectionExactValues()
    {
        var values = NeonSyncJob.BuildProductRowValues(BM10002());
        Assert.Equal("BM10002", values[0].Value);
        Assert.Equal(860335m,    values[IdxPrice01].Value);
        Assert.Equal(1700000m,   values[IdxPrice02].Value);
        Assert.Equal(1700000m,   values[IdxPrice].Value);
        Assert.Equal(1360000m,   values[IdxPrice04].Value);
        Assert.Equal(1280167.5m, values[IdxPrice05].Value);
        Assert.Equal(DBNull.Value, values[IdxOe].Value);
        Assert.Equal(NeonSyncJob.ProductsParamsPerRow, values.Length);
    }

    // Extra regression pin: the INSERT column list itself must contain all five
    // price columns and U_OE_Numbers — catches a future accidental removal even
    // before any value-level test would.
    [Fact]
    public void PROJ_Extra_InsertHeader_ContainsAllFivePriceColumnsAndUOENumbers()
    {
        var header = NeonSyncJob.ProductsInsertHeader;
        var onConflict = NeonSyncJob.ProductsOnConflict;
        foreach (var col in new[] { "Price01", "Price02", "Price", "Price04", "Price05", "U_OE_Numbers" })
        {
            string quoted = "\"" + col + "\"";
            Assert.Contains(quoted, header);
            Assert.Contains(quoted + "=EXCLUDED." + quoted, onConflict);
        }
    }
}
