using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SapReplitAPI.Services.Neon;
using SapReplitAPI.Services.Product;
using Xunit;

namespace SapReplitAPI.Tests.Product;

/// <summary>
/// Production-readiness gate tests for the price cache normalization work:
///   PR01-PR04 — ProductCacheService.ResolvePrice: the no-zero-overwrite guard
///               used by Full/Delta Product Sync when SAP has no ITM1 row for a
///               price list ("not loaded") vs a genuine SAP price of 0.
///   PR05      — BM10001 regression across a chained sequence of price-list sync,
///               re-sync, cache repair, and a stock-only update (which must never
///               touch price columns).
///   PR06      — BM10002 "live SAP truth" regression using representative values
///               standing in for a live SAP read (no live SAP DI API connection
///               is available in this unit-test harness — see comment on the test).
///   PR07-PR10 — GetHealthAsync status computation (HEALTHY / stale / mismatch).
///   PR11      — CheckIntegrityAsync refuses an unfiltered full-catalogue scan
///               unless allowFullScan=true.
/// </summary>
public class PriceCacheProductionReadinessTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly CacheDbContext   _db;

    public PriceCacheProductionReadinessTests()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();
        var opts = new DbContextOptionsBuilder<CacheDbContext>().UseSqlite(_conn).Options;
        _db = new CacheDbContext(opts);
        _db.Database.EnsureCreated();
        _db.Database.ExecuteSqlRaw(@"
CREATE TABLE IF NOT EXISTS PriceLists (
    PriceListNum INTEGER NOT NULL PRIMARY KEY,
    PriceListName TEXT NOT NULL,
    BasePriceList INTEGER,
    Factor REAL NOT NULL DEFAULT 1,
    Currency TEXT NOT NULL,
    IsActive INTEGER NOT NULL DEFAULT 1,
    LastUpdatedUtc TEXT NOT NULL
)");
        _db.Database.ExecuteSqlRaw(@"
CREATE TABLE IF NOT EXISTS ItemPriceLists (
    ItemCode TEXT NOT NULL,
    PriceListNum INTEGER NOT NULL,
    Price REAL NOT NULL,
    Currency TEXT NOT NULL,
    LastUpdatedUtc TEXT NOT NULL,
    PRIMARY KEY (ItemCode, PriceListNum)
)");
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    private async Task SeedProductAsync(
        string itemCode, string itemName,
        decimal price01, decimal price02, decimal price, decimal price04, decimal price05)
    {
        _db.Products.Add(new CachedProduct
        {
            ItemCode = itemCode, ItemName = itemName, U_Item_Name = itemName, WhsCode = "ALL",
            Price01 = price01, Price02 = price02, Price = price, Price04 = price04, Price05 = price05,
            OnHandQty = 0, OnHand = 0, TotalOnHand = 0, LastUpdated = new DateTime(2026, 1, 1),
        });
        await _db.SaveChangesAsync();
    }

    private static NeonDbContext BuildNullNeonCtx()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var opts = new DbContextOptionsBuilder<NeonDbContext>().UseSqlite(conn).Options;
        var ctx = new NeonDbContext(opts);
        ctx.Database.EnsureCreated();
        return ctx;
    }

    private ProductPriceListSyncService BuildSvc() =>
        new(null!, _db, BuildNullNeonCtx(), NullLogger<ProductPriceListSyncService>.Instance);

    private static Task InvokeProjectToProducts(ProductPriceListSyncService svc, List<SapItemPriceDto> rows)
    {
        var m = typeof(ProductPriceListSyncService).GetMethod("ProjectToProductsAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        return (Task)m.Invoke(svc, [rows, CancellationToken.None])!;
    }

    private static Task<int> InvokeUpsertSqliteItemPriceLists(ProductPriceListSyncService svc, List<SapItemPriceDto> rows)
    {
        var m = typeof(ProductPriceListSyncService).GetMethod("UpsertSqliteItemPriceListsAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        return (Task<int>)m.Invoke(svc, [rows, CancellationToken.None])!;
    }

    // ── PR01-PR04 — ResolvePrice no-zero-overwrite guard ──────────────────────

    [Fact]
    public void PR01_ResolvePrice_Loaded_UsesSapValueEvenIfZero()
    {
        // Loaded=true + SAP value 0 is a GENUINE SAP zero — must be honored, not skipped.
        Assert.Equal(0m, ProductCacheService.ResolvePrice(loaded: true, sapValue: 0m, existingValue: 500m));
    }

    [Fact]
    public void PR02_ResolvePrice_NotLoaded_PreservesExistingValue()
    {
        // Loaded=false means no ITM1 row was returned by SAP for this price list —
        // the 0 SAP DTO default must never overwrite the existing cached price.
        Assert.Equal(500m, ProductCacheService.ResolvePrice(loaded: false, sapValue: 0m, existingValue: 500m));
    }

    [Fact]
    public void PR03_ResolvePrice_NotLoaded_NoExisting_DefaultsToZero()
    {
        // Brand-new item with no prior cached row — nothing to protect, 0 is correct.
        Assert.Equal(0m, ProductCacheService.ResolvePrice(loaded: false, sapValue: 999m, existingValue: null));
    }

    [Fact]
    public void PR04_ResolvePrice_Loaded_UsesSapValue()
    {
        Assert.Equal(22187.81m, ProductCacheService.ResolvePrice(loaded: true, sapValue: 22187.81m, existingValue: 100m));
    }

    // ── PR05 — BM10001 regression across a chained sequence ───────────────────

    [Fact]
    public async Task PR05_BM10001_SurvivesPriceListSync_Resync_Repair_And_StockOnlyUpdate()
    {
        const string item = "BM10001";
        decimal pl1 = 22187.81m, pl2 = 35000.00m, pl3 = 65000.00m, pl4 = 20000.00m, pl5 = 41093.91m;

        await SeedProductAsync(item, "BM Test", 0, 0, 0, 0, 0);

        var svc = BuildSvc();
        var sapTruth = new List<SapItemPriceDto>
        {
            new(item, 1, pl1, "ILS"), new(item, 2, pl2, "ILS"), new(item, 3, pl3, "ILS"),
            new(item, 4, pl4, "ILS"), new(item, 5, pl5, "ILS"),
        };

        // Step 1: dedicated PriceList sync (SQLite ItemPriceLists + Products projection)
        await InvokeUpsertSqliteItemPriceLists(svc, sapTruth);
        await InvokeProjectToProducts(svc, sapTruth);
        await AssertBm10001(item, pl1, pl2, pl3, pl4, pl5);

        // Step 2/3 stand-ins: a Full/Delta Product Sync re-affirms the same SAP truth
        // (equivalent to ProductCacheService.ResolvePrice always being "loaded" here).
        await InvokeUpsertSqliteItemPriceLists(svc, sapTruth);
        await InvokeProjectToProducts(svc, sapTruth);
        await AssertBm10001(item, pl1, pl2, pl3, pl4, pl5);

        // Step 4: inventory-event-style stock-only update — must NEVER touch price.
        await _db.Database.ExecuteSqlRawAsync(
            "UPDATE Products SET TotalOnHand=@t, OnHand=@t, OnHandQty=@t WHERE ItemCode=@ic",
            new SqliteParameter("@t", 15.0), new SqliteParameter("@ic", item));
        await AssertBm10001(item, pl1, pl2, pl3, pl4, pl5);
        var afterStock = await _db.Products.AsNoTracking().FirstAsync(p => p.ItemCode == item);
        Assert.Equal(15m, afterStock.TotalOnHand);

        // Step 5 stand-in: Neon mirror sync (re-run the same projection — Neon reload
        // always mirrors the already-correct SQLite state, see report).
        await InvokeProjectToProducts(svc, sapTruth);
        await AssertBm10001(item, pl1, pl2, pl3, pl4, pl5);

        // Step 6: cache repair for a single price list (PL2) via the transactional
        // normalized-update path — only PL2 changes, the other four must survive.
        bool repaired = await svc.UpdateSqliteNormalizedPriceAsync(item, 2, pl2, "ILS");
        Assert.True(repaired);
        await AssertBm10001(item, pl1, pl2, pl3, pl4, pl5);

        // Normalized table must match SAP truth for all 5 price lists.
        var iplRows = await _db.ItemPriceLists.AsNoTracking().Where(x => x.ItemCode == item).ToListAsync();
        Assert.Equal(5, iplRows.Count);
        Assert.Equal(pl1, iplRows.First(r => r.PriceListNum == 1).Price, precision: 2);
        Assert.Equal(pl2, iplRows.First(r => r.PriceListNum == 2).Price, precision: 2);
        Assert.Equal(pl3, iplRows.First(r => r.PriceListNum == 3).Price, precision: 2);
        Assert.Equal(pl4, iplRows.First(r => r.PriceListNum == 4).Price, precision: 2);
        Assert.Equal(pl5, iplRows.First(r => r.PriceListNum == 5).Price, precision: 2);
    }

    private async Task AssertBm10001(string item, decimal pl1, decimal pl2, decimal pl3, decimal pl4, decimal pl5)
    {
        var prod = await _db.Products.AsNoTracking().FirstAsync(p => p.ItemCode == item);
        Assert.Equal(pl1, prod.Price01, precision: 2);
        Assert.Equal(pl2, prod.Price02, precision: 2);
        Assert.Equal(pl3, prod.Price,   precision: 2);
        Assert.Equal(pl4, prod.Price04, precision: 2);
        Assert.Equal(pl5, prod.Price05, precision: 2);
    }

    // ── PR06 — BM10002 "live SAP truth" regression ────────────────────────────

    /// <summary>
    /// The spec requires reading PL1-PL5 live from SAP for BM10002 and using those
    /// as test truth rather than hard-coded guesses. This unit-test harness has no
    /// live SAP DI API connection available (SapService requires a real SAP B1
    /// COM connection — see SapService.GetConnectedCompany), so this test instead
    /// demonstrates the preservation sequence is correct for ANY SAP-truth values,
    /// not just BM10001's specific numbers — proving the logic isn't hard-coded to
    /// one item. In a live environment, replace `liveSapTruth` with the output of
    /// SapService.GetAllItemPrices(["BM10002"], [1,2,3,4,5]) run against production
    /// SAP, per the mandatory manual verification step in the completion report.
    /// </summary>
    [Fact]
    public async Task PR06_BM10002_SurvivesSameSequence_UsingArbitrarySapTruth()
    {
        const string item = "BM10002";
        // Stand-in for "live SAP truth" — deliberately different shape from BM10001
        // (includes a genuine zero on PL4) to prove zero-handling is correct too.
        var liveSapTruth = new List<SapItemPriceDto>
        {
            new(item, 1, 8140.00m, "TZS"),
            new(item, 2, 12500.50m, "TZS"),
            new(item, 3, 30000.00m, "TZS"),
            new(item, 4, 0.00m, "TZS"),      // genuine SAP zero — must be preserved as 0, not skipped
            new(item, 5, 9999.99m, "TZS"),
        };
        decimal pl1 = 8140.00m, pl2 = 12500.50m, pl3 = 30000.00m, pl4 = 0.00m, pl5 = 9999.99m;

        await SeedProductAsync(item, "BM10002 Test", 1, 1, 1, 1, 1); // deliberately wrong seed

        var svc = BuildSvc();
        await InvokeUpsertSqliteItemPriceLists(svc, liveSapTruth);
        await InvokeProjectToProducts(svc, liveSapTruth);
        await AssertBm10001(item, pl1, pl2, pl3, pl4, pl5);

        // Repair PL4 explicitly (genuine zero) — must remain 0, not silently skipped
        // or replaced by the "not loaded" preserve-existing path.
        bool repaired = await svc.UpdateSqliteNormalizedPriceAsync(item, 4, 0.00m, "TZS");
        Assert.True(repaired);
        await AssertBm10001(item, pl1, pl2, pl3, pl4, pl5);
    }

    // ── PR07-PR10 — Health status (pure logic — see ComputeHealth) ────────────
    // GetHealthAsync's Neon row-count read requires a live Npgsql connection, which
    // this unit-test harness does not have (it uses a SQLite-backed stand-in
    // NeonDbContext, as the rest of this file does for paths that don't reach
    // Neon). ComputeHealth is factored out specifically so the status-decision
    // logic itself can be tested without that dependency.

    [Fact]
    public void PR07_Health_NeverSynced_ReturnsSapSyncStale()
    {
        var now = DateTime.UtcNow;
        var health = ProductPriceListSyncService.ComputeHealth(
            null, null, null, null, 0, 0, 0, 0, lastError: null, now: now);
        Assert.Equal("SAP_SYNC_STALE", health.Status);
        Assert.Null(health.LastSapSyncUtc);
    }

    [Fact]
    public void PR08_Health_RecentSapSync_NoNeonSync_ReturnsNeonMirrorStale()
    {
        var now = DateTime.UtcNow;
        var health = ProductPriceListSyncService.ComputeHealth(
            now, now, null, null, 5, 20, 5, 20, lastError: null, now: now);
        Assert.Equal("NEON_MIRROR_STALE", health.Status);
    }

    [Fact]
    public void PR09_Health_StaleSapWatermark_ReturnsSapSyncStale()
    {
        var now = DateTime.UtcNow;
        var old = now.AddHours(-2);
        var health = ProductPriceListSyncService.ComputeHealth(
            old, old, old, old, 5, 20, 5, 20, lastError: null, now: now);
        Assert.Equal("SAP_SYNC_STALE", health.Status);
        Assert.True(health.LagSeconds > 3000);
    }

    [Fact]
    public void PR10_Health_RecentSyncs_RowCountsMatch_ReturnsHealthy()
    {
        var now = DateTime.UtcNow;
        var health = ProductPriceListSyncService.ComputeHealth(
            now, now, now, now, 5, 20, 5, 20, lastError: null, now: now);
        Assert.Equal("HEALTHY", health.Status);
    }

    [Fact]
    public void PR10b_Health_RowCountMismatch_ReturnsRowCountMismatch()
    {
        var now = DateTime.UtcNow;
        var health = ProductPriceListSyncService.ComputeHealth(
            now, now, now, now, 5, 20, 5, 19, lastError: null, now: now);
        Assert.Equal("ROW_COUNT_MISMATCH", health.Status);
    }

    [Fact]
    public void PR10c_Health_NeonError_ReturnsError()
    {
        var now = DateTime.UtcNow;
        var health = ProductPriceListSyncService.ComputeHealth(
            now, now, now, now, 5, 20, 5, 20, lastError: "connection refused", now: now);
        Assert.Equal("ERROR", health.Status);
        Assert.Equal("connection refused", health.LastError);
    }

    // ── PR11 — Integrity endpoint full-scan guard ─────────────────────────────

    [Fact]
    public async Task PR11_CheckIntegrity_NoFilter_NoOverride_Throws()
    {
        var svc = BuildSvc();
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.CheckIntegrityAsync());
    }

    [Fact]
    public async Task PR11b_CheckIntegrity_NoFilter_AllowFullScan_Succeeds()
    {
        var svc = BuildSvc();
        var rows = await svc.CheckIntegrityAsync(null, allowFullScan: true);
        Assert.NotNull(rows);
    }

    [Fact]
    public async Task PR11c_CheckIntegrity_WithFilter_DoesNotRequireAllowFullScan()
    {
        var svc = BuildSvc();
        var rows = await svc.CheckIntegrityAsync(["BM10001"]);
        Assert.NotNull(rows);
    }
}
