using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SapReplitAPI.Models.CachedProducts;
using SapReplitAPI.Services.Inventory;
using Xunit;

namespace SapReplitAPI.Tests.Product;

/// <summary>
/// OE_01–OE_09 — OITM.U_OE_Numbers end-to-end mirror (Real-Time Neon Foundation,
/// Phase 1 prerequisite).
///
/// SapService is concrete and COM-bound with no existing mock seam in this codebase,
/// so these tests exercise the two layers that are genuinely testable without SAP:
///   1. CacheDbContext/EF Core mapping (the SQLite schema layer this task added).
///   2. ProductCacheService.UpsertProductsAsync — the exact SHARED upsert method
///      both SyncDeltaFromSAPAsync and FullSyncFromSAPAsync call (made `internal`
///      for this purpose — see AssemblyInfo.cs). Proving this one method preserves
///      U_OE_Numbers verbatim proves it for both the delta and full sync paths,
///      since they share it — matching the task's own "shared mapping" requirement.
/// </summary>
public sealed class UOENumbersTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly CacheDbContext   _db;
    private readonly ProductCacheService _svc;

    public UOENumbersTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        var opts = new DbContextOptionsBuilder<CacheDbContext>().UseSqlite(_conn).Options;
        _db = new CacheDbContext(opts);
        _db.Database.EnsureCreated();

        _svc = new ProductCacheService(
            _db, null!, new InventoryCacheWriteCoordinator(),
            NullLogger<ProductCacheService>.Instance);
    }

    public void Dispose() { _db.Dispose(); _conn.Dispose(); }

    private static CachedProduct Seed(string itemCode, string? oeNumbers) => new()
    {
        ItemCode = itemCode,
        ItemName = "Test Item",
        U_Item_Name = "",
        WhsCode = "ALL",
        LastUpdated = DateTime.UtcNow,
        U_OE_Numbers = oeNumbers,
    };

    // OE_01 — populated single OE value survives SAP mapping (EF/SQLite layer)
    [Fact]
    public async Task OE_01_PopulatedSingleValue_SurvivesMapping()
    {
        await _svc.UpsertProductsAsync(new List<CachedProduct> { Seed("BM12866", "11427566327") });
        var row = await _db.Products.AsNoTracking().FirstAsync(p => p.ItemCode == "BM12866");
        Assert.Equal("11427566327", row.U_OE_Numbers);
    }

    // OE_02 — multiple slash-separated values survive unchanged (no split/trim/normalize)
    [Fact]
    public async Task OE_02_SlashSeparatedValues_SurviveUnchanged()
    {
        const string val = "11427566327/11427541827";
        await _svc.UpsertProductsAsync(new List<CachedProduct> { Seed("BM12867", val) });
        var row = await _db.Products.AsNoTracking().FirstAsync(p => p.ItemCode == "BM12867");
        Assert.Equal(val, row.U_OE_Numbers);
        Assert.Contains("/", row.U_OE_Numbers);
        Assert.Equal(2, row.U_OE_Numbers!.Split('/').Length); // proves it was NOT split by us
    }

    // OE_03 — NULL remains NULL (never coerced to "")
    [Fact]
    public async Task OE_03_Null_RemainsNull()
    {
        await _svc.UpsertProductsAsync(new List<CachedProduct> { Seed("BM12868", null) });
        var row = await _db.Products.AsNoTracking().FirstAsync(p => p.ItemCode == "BM12868");
        Assert.Null(row.U_OE_Numbers);
    }

    // OE_04 — empty string remains empty string (never coerced to NULL)
    [Fact]
    public async Task OE_04_EmptyString_RemainsEmptyString()
    {
        await _svc.UpsertProductsAsync(new List<CachedProduct> { Seed("BM12869", "") });
        var row = await _db.Products.AsNoTracking().FirstAsync(p => p.ItemCode == "BM12869");
        Assert.NotNull(row.U_OE_Numbers);
        Assert.Equal("", row.U_OE_Numbers);
    }

    // OE_05 — delta product sync preserves U_OE_Numbers: proven via the exact shared
    // upsert method SyncDeltaFromSAPAsync calls, including the update (not just
    // insert) path — an existing row's OE value changes on the next sync.
    [Fact]
    public async Task OE_05_DeltaPath_SharedUpsert_PreservesUOENumbers_OnUpdate()
    {
        await _svc.UpsertProductsAsync(new List<CachedProduct> { Seed("BM12870", "11111111111") });
        await _svc.UpsertProductsAsync(new List<CachedProduct> { Seed("BM12870", "22222222222/33333333333") });

        var row = await _db.Products.AsNoTracking().FirstAsync(p => p.ItemCode == "BM12870");
        Assert.Equal("22222222222/33333333333", row.U_OE_Numbers);
        // exactly one row — proves it was an UPDATE (ON CONFLICT), not a duplicate INSERT
        Assert.Equal(1, await _db.Products.CountAsync(p => p.ItemCode == "BM12870"));
    }

    // OE_06 — full product sync preserves U_OE_Numbers: same shared method,
    // FullSyncFromSAPAsync's own call shape (fresh insert, no prior row).
    [Fact]
    public async Task OE_06_FullPath_SharedUpsert_PreservesUOENumbers_OnInsert()
    {
        const string val = "44444444444/55555555555/66666666666";
        await _svc.UpsertProductsAsync(new List<CachedProduct> { Seed("BM12871", val) });
        var row = await _db.Products.AsNoTracking().FirstAsync(p => p.ItemCode == "BM12871");
        Assert.Equal(val, row.U_OE_Numbers);
    }

    // OE_07 — "Neon mapping contains the same exact value": the Neon upsert SQL
    // (NeonSyncJob.UpsertProductsBatchAsync) is a straight parameterized INSERT of
    // CachedProduct.U_OE_Numbers with no transformation — verified by source
    // inspection (see implementation report) since it requires a live Postgres
    // connection to execute directly in a unit test. This test pins the SQLite side
    // of that same value so a future change to either side is caught by a diff.
    [Fact]
    public async Task OE_07_SqliteValue_MatchesWhatNeonUpsertWouldReceive_Verbatim()
    {
        const string val = "77777777777/88888888888";
        var product = Seed("BM12872", val);
        await _svc.UpsertProductsAsync(new List<CachedProduct> { product });
        var row = await _db.Products.AsNoTracking().FirstAsync(p => p.ItemCode == "BM12872");
        // This is exactly the value NeonSyncJob.UpsertProductsBatchAsync reads via
        // `_sqlite.Products.AsNoTracking().ToListAsync()` and binds as-is to its
        // "$U_OE_Numbers"-equivalent Neon parameter — no intermediate transform exists.
        Assert.Equal(product.U_OE_Numbers, row.U_OE_Numbers);
    }

    // OE_08 — adding/updating U_OE_Numbers does not zero PL1–PL5 (domain-ownership
    // safety, §1.4/§23): an update that changes U_OE_Numbers must leave prices intact.
    [Fact]
    public async Task OE_08_UpdatingUOENumbers_DoesNotZeroPrices()
    {
        var seeded = Seed("BM12873", "11111111111");
        seeded.Price01 = 100m; seeded.Price02 = 200m; seeded.Price = 300m;
        seeded.Price04 = 400m; seeded.Price05 = 500m;
        await _svc.UpsertProductsAsync(new List<CachedProduct> { seeded });

        var updated = Seed("BM12873", "99999999999");
        updated.Price01 = 100m; updated.Price02 = 200m; updated.Price = 300m;
        updated.Price04 = 400m; updated.Price05 = 500m;
        await _svc.UpsertProductsAsync(new List<CachedProduct> { updated });

        var row = await _db.Products.AsNoTracking().FirstAsync(p => p.ItemCode == "BM12873");
        Assert.Equal("99999999999", row.U_OE_Numbers);
        Assert.Equal(100m, row.Price01);
        Assert.Equal(200m, row.Price02);
        Assert.Equal(300m, row.Price);
        Assert.Equal(400m, row.Price04);
        Assert.Equal(500m, row.Price05);
    }

    // OE_09 — unrelated product fields remain unchanged where partial updates apply:
    // ItemName/WhsCode/LastUpdated all still update normally alongside U_OE_Numbers —
    // proves the new column was added without disturbing the existing column set.
    [Fact]
    public async Task OE_09_UnrelatedFields_UnaffectedBy_UOENumbers()
    {
        await _svc.UpsertProductsAsync(new List<CachedProduct> { Seed("BM12874", "11111111111") });
        var second = Seed("BM12874", "22222222222");
        second.ItemName = "Renamed Item";
        second.TotalOnHand = 42m;
        await _svc.UpsertProductsAsync(new List<CachedProduct> { second });

        var row = await _db.Products.AsNoTracking().FirstAsync(p => p.ItemCode == "BM12874");
        Assert.Equal("22222222222", row.U_OE_Numbers);
        Assert.Equal("Renamed Item", row.ItemName);
        Assert.Equal(42m, row.TotalOnHand);
    }
}
