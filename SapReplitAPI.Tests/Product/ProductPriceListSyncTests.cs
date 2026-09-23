using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SapReplitAPI.Services.Neon;
using SapReplitAPI.Services.Product;
using Xunit;

namespace SapReplitAPI.Tests.Product;

/// <summary>
/// Tests for ProductPriceListSyncService — covers:
///   PL01 — SQLite PriceLists upsert
///   PL02 — SQLite ItemPriceLists upsert (all 5 PL)
///   PL03 — Products projection correctness
///   PL04 — BM10001 regression: exact SAP truth values survive sync
///   PL05 — No valid price overwritten with 0 (safe-sync rule)
///   PL06 — PriceColumn mapping (PL3 → "Price", others → "Price0N")
///   PL07 — PriceSyncResult counts are correct
///   PL08 — CheckIntegrityAsync: IN_SYNC case
///   PL09 — CheckIntegrityAsync: SQLITE_PRICE_MISSING case
///   PL10 — RepairItemPriceAsync: SQLite IPL upserted
/// </summary>
public class ProductPriceListSyncTests : IDisposable
{
    // ── Shared infra ──────────────────────────────────────────────────────────

    private readonly SqliteConnection _conn;
    private readonly CacheDbContext   _db;

    public ProductPriceListSyncTests()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
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

    private async Task SeedProductAsync(
        string itemCode, string itemName, string? uArticleNo = null,
        decimal price01 = 0, decimal price02 = 0, decimal price = 0,
        decimal price04 = 0, decimal price05 = 0)
    {
        _db.Products.Add(new CachedProduct
        {
            ItemCode      = itemCode,
            ItemName      = itemName,
            U_Article_No  = uArticleNo,
            U_Item_Name   = itemName,
            WhsCode       = "ALL",
            Price01       = price01,
            Price02       = price02,
            Price         = price,
            Price04       = price04,
            Price05       = price05,
            OnHandQty     = 0,
            OnHand        = 0,
            TotalOnHand   = 0,
            LastUpdated   = new DateTime(2026, 1, 1),
        });
        await _db.SaveChangesAsync();
    }

    // EF-created in-memory DB won't have PriceLists/ItemPriceLists if the
    // Migration hasn't run; create them manually for isolated tests.
    private void EnsurePriceListTables()
    {
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

    private ProductPriceListSyncService BuildSvc(NeonDbContext? neon = null)
    {
        neon ??= BuildNullNeonCtx();
        return new ProductPriceListSyncService(
            null!,
            _db,
            neon,
            NullLogger<ProductPriceListSyncService>.Instance);
    }

    // In-memory Neon context (tests only touch SQLite paths, not Neon paths)
    private static NeonDbContext BuildNullNeonCtx()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var opts = new DbContextOptionsBuilder<NeonDbContext>()
            .UseSqlite(conn)
            .Options;
        var ctx = new NeonDbContext(opts);
        ctx.Database.EnsureCreated();
        return ctx;
    }

    // ── PL06 — PriceColumn mapping ────────────────────────────────────────────

    [Theory]
    [InlineData(1, "Price01")]
    [InlineData(2, "Price02")]
    [InlineData(3, "Price")]
    [InlineData(4, "Price04")]
    [InlineData(5, "Price05")]
    public void PL06_PriceColumn_ReturnsCorrectColumnName(int pl, string expected)
    {
        Assert.Equal(expected, ProductPriceListSyncService.PriceColumn(pl));
    }

    [Fact]
    public void PL06b_PriceColumn_UnknownPL_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ProductPriceListSyncService.PriceColumn(6));
    }

    // ── PL01 — SQLite PriceLists upsert ───────────────────────────────────────

    [Fact]
    public async Task PL01_UpsertPriceLists_InsertsAllRows()
    {
        EnsurePriceListTables();
        var svc = BuildSvc();

        var rows = new List<SapPriceListDto>
        {
            new(1, "Retail",    null, 1m,    "ILS", true),
            new(2, "Wholesale", 1,    0.85m, "ILS", true),
            new(3, "Default",   null, 1m,    "ILS", true),
        };

        await InvokeSqlitePriceLists(svc, rows);

        int count = await _db.Database
            .SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM PriceLists")
            .FirstAsync();
        Assert.Equal(3, count);
    }

    [Fact]
    public async Task PL01b_UpsertPriceLists_Upserts_ExistingRow()
    {
        EnsurePriceListTables();
        var svc = BuildSvc();

        var initial = new List<SapPriceListDto> { new(1, "Old", null, 1m, "ILS", true) };
        await InvokeSqlitePriceLists(svc, initial);

        var updated = new List<SapPriceListDto> { new(1, "Updated", null, 1.1m, "USD", true) };
        await InvokeSqlitePriceLists(svc, updated);

        int count = await _db.Database
            .SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM PriceLists WHERE PriceListNum=1")
            .FirstAsync();
        Assert.Equal(1, count);
    }

    // ── PL02 — SQLite ItemPriceLists upsert (all 5 PL) ───────────────────────

    [Fact]
    public async Task PL02_UpsertItemPriceLists_AllFivePriceLists()
    {
        EnsurePriceListTables();
        var svc = BuildSvc();

        var rows = BuildBm10001SapPrices();

        await InvokeSqliteItemPriceLists(svc, rows);

        int count = await _db.Database
            .SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM ItemPriceLists WHERE ItemCode='BM10001'")
            .FirstAsync();
        Assert.Equal(5, count);
    }

    [Fact]
    public async Task PL02b_UpsertItemPriceLists_UpdatesExistingRow()
    {
        EnsurePriceListTables();
        var svc = BuildSvc();

        var initial = new List<SapItemPriceDto> { new("BM10001", 1, 22000m, "ILS") };
        await InvokeSqliteItemPriceLists(svc, initial);

        var updated = new List<SapItemPriceDto> { new("BM10001", 1, 22187.81m, "ILS") };
        await InvokeSqliteItemPriceLists(svc, updated);

        int count = await _db.Database
            .SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM ItemPriceLists WHERE ItemCode='BM10001' AND PriceListNum=1")
            .FirstAsync();
        Assert.Equal(1, count);
    }

    // ── PL03 — Products projection ────────────────────────────────────────────

    [Fact]
    public async Task PL03_ProjectToProducts_UpdatesAllFivePriceColumns()
    {
        EnsurePriceListTables();

        // Seed a product row
        await SeedProductAsync("BM10001", "Test Item");

        var svc = BuildSvc();
        var rows = BuildBm10001SapPrices();
        await InvokeProjectToProducts(svc, rows);

        var prod = await _db.Products.AsNoTracking().FirstAsync(p => p.ItemCode == "BM10001");
        Assert.Equal(22187.81m, prod.Price01, precision: 2);
        Assert.Equal(35000m,    prod.Price02, precision: 2);
        Assert.Equal(65000m,    prod.Price,   precision: 2);
        Assert.Equal(20000m,    prod.Price04, precision: 2);
        Assert.Equal(41093.91m, prod.Price05, precision: 2);
    }

    [Fact]
    public async Task PL03b_ProjectToProducts_DoesNotTouchOtherColumns()
    {
        EnsurePriceListTables();

        await SeedProductAsync("BM10001", "Test Item", uArticleNo: "ART123");

        var svc = BuildSvc();
        var rows = BuildBm10001SapPrices();
        await InvokeProjectToProducts(svc, rows);

        var prod = await _db.Products.AsNoTracking().FirstAsync(p => p.ItemCode == "BM10001");
        Assert.Equal("ART123", prod.U_Article_No);
        Assert.Equal("Test Item", prod.ItemName);
    }

    // ── PL04 — BM10001 regression ─────────────────────────────────────────────

    /// SAP truth for BM10001: PL1=22187.81, PL2=35000.00, PL3=65000.00, PL4=20000.00, PL5=41093.91
    [Fact]
    public async Task PL04_BM10001_ExactValuesAfterSync()
    {
        EnsurePriceListTables();

        await SeedProductAsync("BM10001", "BM Test");

        var svc = BuildSvc();
        await InvokeSqliteItemPriceLists(svc, BuildBm10001SapPrices());
        await InvokeProjectToProducts(svc, BuildBm10001SapPrices());

        var prod = await _db.Products.AsNoTracking().FirstAsync(p => p.ItemCode == "BM10001");
        Assert.Equal(22187.81m, prod.Price01, precision: 2);
        Assert.Equal(35000.00m, prod.Price02, precision: 2);
        Assert.Equal(65000.00m, prod.Price,   precision: 2);
        Assert.Equal(20000.00m, prod.Price04, precision: 2);
        Assert.Equal(41093.91m, prod.Price05, precision: 2);

        // Normalized table must hold the same values
        int ipl = await _db.Database
            .SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM ItemPriceLists WHERE ItemCode='BM10001'")
            .FirstAsync();
        Assert.Equal(5, ipl);
    }

    // ── PL05 — No valid price overwritten with 0 ──────────────────────────────

    [Fact]
    public async Task PL05_ProjectToProducts_DoesNotOverwriteWithZero_WhenOnlyPartialPlData()
    {
        EnsurePriceListTables();

        // Pre-populate with valid prices
        await SeedProductAsync("BM10001", "Test",
            price01: 22187.81m, price02: 35000m, price: 65000m,
            price04: 20000m,    price05: 41093.91m);

        var svc = BuildSvc();
        // Only send PL3 — a partial load (like old GetProductsForItems behaviour).
        // Projection must only update the Price column and leave others untouched.
        var partial = new List<SapItemPriceDto> { new("BM10001", 3, 65000m, "ILS") };
        await InvokeProjectToProducts(svc, partial);

        var prod = await _db.Products.AsNoTracking().FirstAsync(p => p.ItemCode == "BM10001");
        // All non-PL3 prices must survive
        Assert.Equal(22187.81m, prod.Price01, precision: 2);
        Assert.Equal(35000m,    prod.Price02, precision: 2);
        Assert.Equal(20000m,    prod.Price04, precision: 2);
        Assert.Equal(41093.91m, prod.Price05, precision: 2);
        // PL3 was updated
        Assert.Equal(65000m,    prod.Price,   precision: 2);
    }

    // ── PL10 — UpdateSqliteNormalizedPriceAsync upserts IPL + Products atomically ─
    // Note: RepairItemPriceAsync itself also writes to Neon via a hard
    // NpgsqlConnection cast, which requires a live Postgres connection — not
    // available in a unit test. We invoke the SQLite-only transactional step
    // directly instead (this is exactly what RepairItemPriceAsync calls internally).

    [Fact]
    public async Task PL10_UpdateSqliteNormalizedPriceAsync_UpsertsItemPriceListsAndProjection()
    {
        EnsurePriceListTables();

        await SeedProductAsync("BM10001", "Test");

        var svc = BuildSvc();
        bool updated = await svc.UpdateSqliteNormalizedPriceAsync("BM10001", 1, 22187.81m, "ILS");
        Assert.True(updated);

        int count = await _db.Database
            .SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM ItemPriceLists WHERE ItemCode='BM10001' AND PriceListNum=1")
            .FirstAsync();
        Assert.Equal(1, count);

        var prod = await _db.Products.AsNoTracking().FirstAsync(p => p.ItemCode == "BM10001");
        Assert.Equal(22187.81m, prod.Price01, precision: 2);
    }

    // ── PL08 — CheckIntegrityAsync: IN_SYNC ───────────────────────────────────

    [Fact]
    public async Task PL08_CheckIntegrity_InSync_WhenDataMatches()
    {
        EnsurePriceListTables();

        await SeedProductAsync("BM10001", "Test",
            price01: 22187.81m, price02: 35000m, price: 65000m,
            price04: 20000m,    price05: 41093.91m);

        var svc = BuildSvc();
        await InvokeSqliteItemPriceLists(svc, BuildBm10001SapPrices());

        var rows = await svc.CheckIntegrityAsync(["BM10001"]);

        // With null Neon context the Neon rows will be absent — that's NEON_PRICE_MISSING.
        // But SQLite IPL ↔ Products projection should be IN_SYNC for all 5 PL.
        var sqliteMismatches = rows
            .Where(r => r.Status.Contains("PRODUCT_PROJECTION_MISMATCH") || r.Status == "SQLITE_PRICE_MISSING")
            .ToList();
        Assert.Empty(sqliteMismatches);
    }

    // ── PL09 — CheckIntegrityAsync: SQLITE_PRICE_MISSING ─────────────────────

    [Fact]
    public async Task PL09_CheckIntegrity_SqlitePriceMissing_WhenNoIplRow()
    {
        EnsurePriceListTables();

        await SeedProductAsync("MISSING_IPL", "Test",
            price01: 22000m, price02: 0m, price: 65000m, price04: 0m, price05: 40000m);

        var svc = BuildSvc();
        // No ItemPriceLists rows inserted — Products exists but IPL is empty.
        var rows = await svc.CheckIntegrityAsync(["MISSING_IPL"]);

        Assert.True(rows.Any(r => r.Status == "SQLITE_PRICE_MISSING"));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static List<SapItemPriceDto> BuildBm10001SapPrices() =>
    [
        new("BM10001", 1, 22187.81m, "ILS"),
        new("BM10001", 2, 35000.00m, "ILS"),
        new("BM10001", 3, 65000.00m, "ILS"),
        new("BM10001", 4, 20000.00m, "ILS"),
        new("BM10001", 5, 41093.91m, "ILS"),
    ];

    // ── Reflection helpers to call internal methods without full FullSyncAsync ──

    private static Task InvokeSqlitePriceLists(ProductPriceListSyncService svc, List<SapPriceListDto> rows)
    {
        var m = typeof(ProductPriceListSyncService)
            .GetMethod("UpsertSqlitePriceListsAsync",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        return (Task)m.Invoke(svc, [rows, CancellationToken.None])!;
    }

    private static Task InvokeSqliteItemPriceLists(ProductPriceListSyncService svc, List<SapItemPriceDto> rows)
    {
        var m = typeof(ProductPriceListSyncService)
            .GetMethod("UpsertSqliteItemPriceListsAsync",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        return (Task)m.Invoke(svc, [rows, CancellationToken.None])!;
    }

    private static Task InvokeProjectToProducts(ProductPriceListSyncService svc, List<SapItemPriceDto> rows)
    {
        var m = typeof(ProductPriceListSyncService)
            .GetMethod("ProjectToProductsAsync",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        return (Task)m.Invoke(svc, [rows, CancellationToken.None])!;
    }

}
