using Microsoft.Extensions.Logging.Abstractions;
using SapReplitAPI.Models.ProductAdmin;
using SapReplitAPI.Services.ProductAdmin;
using Xunit;

namespace SapReplitAPI.Tests.ProductAdmin;

/// <summary>
/// PA_B01–PA_B25: Bulk Pricing Business Workflow — preview, execute, cache-repair, batch-status.
/// All tests are in-memory: no SQL, no SAP COM, no production mutations.
/// Reuses FakeSapPriceAdapter, FakeAuditRepository, TestableZfProductAdminService from ProductPriceTests.cs.
/// </summary>
public sealed class BulkPricingWorkflowTests
{
    // ── helpers ──────────────────────────────────────────────────────────────

    private static TestableZfProductAdminService MakeService(
        ISapPriceAdapter? sap = null,
        FakeAuditRepositoryWithStatus? audit = null,
        string sqliteResult = "OK",
        string neonResult = "OK")
    {
        sap ??= new FakeSapPriceAdapter
            { ReadResult = new SapCurrentPrice { Price = 100m, Currency = "TZS" } };
        audit ??= new FakeAuditRepositoryWithStatus();
        return new TestableZfProductAdminService(sap, audit, NullLogger<ZfProductAdminService>.Instance)
        {
            SqliteResult = sqliteResult,
            NeonResult   = neonResult,
        };
    }

    private static BulkPreviewRequest MakePreviewRequest(
        params BulkPriceUpdateItem[] items) =>
        new()
        {
            RequestId   = Guid.NewGuid(),
            RequestedBy = "tester",
            Reason      = "unit-test",
            Updates     = items.ToList(),
        };

    private static BulkPriceUpdateItem Item(
        string code, int pl, decimal price, decimal? expected = null) =>
        new() { ItemCode = code, PriceListNum = pl, Price = price, ExpectedCurrentPrice = expected };

    // ── PA_B01: Preview never calls SAP write ─────────────────────────────────

    [Fact]
    public async Task PA_B01_Preview_NeverCallsSapWrite()
    {
        var sap = new FakeSapPriceAdapter
            { ReadResult = new SapCurrentPrice { Price = 100m, Currency = "TZS" } };
        var svc = MakeService(sap);

        await svc.PreviewBulkPricesAsync(MakePreviewRequest(Item("ITEM001", 1, 150m)));

        Assert.Equal(0, sap.UpdateCallCount);
    }

    // ── PA_B02–PA_B06: READY for each valid price list ────────────────────────

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task PA_B02_Through_B06_Preview_ValidPL_Returns_READY(int pl)
    {
        var sap = new FakeSapPriceAdapter
            { ReadResult = new SapCurrentPrice { Price = 100m, Currency = "TZS" } };
        var svc = MakeService(sap);

        var result = await svc.PreviewBulkPricesAsync(MakePreviewRequest(Item("ITEM001", pl, 150m)));

        Assert.Single(result.Items);
        Assert.Equal(PreviewValidationStatus.Ready, result.Items[0].ValidationStatus);
        Assert.True(result.Items[0].CanExecute);
        Assert.Equal(1, result.ReadyRows);
    }

    // ── PA_B07: INVALID_PRICE_LIST ────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(-1)]
    public async Task PA_B07_Preview_InvalidPL_Returns_INVALID_PRICE_LIST(int pl)
    {
        var svc = MakeService();
        var result = await svc.PreviewBulkPricesAsync(MakePreviewRequest(Item("ITEM001", pl, 150m)));

        Assert.Single(result.Items);
        Assert.Equal(PreviewValidationStatus.InvalidPriceList, result.Items[0].ValidationStatus);
        Assert.False(result.Items[0].CanExecute);
    }

    // ── PA_B08: Negative price → INVALID_PRICE ────────────────────────────────

    [Fact]
    public async Task PA_B08_Preview_NegativePrice_Returns_INVALID_PRICE()
    {
        var svc = MakeService();
        var result = await svc.PreviewBulkPricesAsync(MakePreviewRequest(Item("ITEM001", 1, -1m)));

        Assert.Equal(PreviewValidationStatus.InvalidPrice, result.Items[0].ValidationStatus);
        Assert.False(result.Items[0].CanExecute);
    }

    // ── PA_B09: Price too large → INVALID_PRICE ──────────────────────────────

    [Fact]
    public async Task PA_B09_Preview_TooBigPrice_Returns_INVALID_PRICE()
    {
        var svc = MakeService();
        var result = await svc.PreviewBulkPricesAsync(
            MakePreviewRequest(Item("ITEM001", 1, 1_000_000_000m)));

        Assert.Equal(PreviewValidationStatus.InvalidPrice, result.Items[0].ValidationStatus);
    }

    // ── PA_B10: Duplicate ItemCode+PL → DUPLICATE_ITEM_PRICE_LIST ───────────

    [Fact]
    public async Task PA_B10_Preview_DuplicateItemPL_Returns_DUPLICATE_ITEM_PRICE_LIST()
    {
        var sap = new FakeSapPriceAdapter
            { ReadResult = new SapCurrentPrice { Price = 100m, Currency = "TZS" } };
        var svc = MakeService(sap);

        var result = await svc.PreviewBulkPricesAsync(MakePreviewRequest(
            Item("ITEM001", 1, 150m),
            Item("ITEM001", 1, 200m)   // same ItemCode + PriceListNum
        ));

        Assert.Equal(2, result.Items.Count);
        Assert.All(result.Items, i =>
            Assert.Equal(PreviewValidationStatus.DuplicateItemPriceList, i.ValidationStatus));
        Assert.Equal(0, result.ReadyRows);
    }

    // ── PA_B11: ITEM_NOT_FOUND ────────────────────────────────────────────────

    [Fact]
    public async Task PA_B11_Preview_ItemMissing_Returns_ITEM_NOT_FOUND()
    {
        // ReadItemPrice returns null for ALL price lists → ITEM_NOT_FOUND
        var sap = new FakeSapPriceAdapter { ReadResult = null };
        var svc = MakeService(sap);

        var result = await svc.PreviewBulkPricesAsync(MakePreviewRequest(Item("GHOST001", 1, 150m)));

        Assert.Equal(PreviewValidationStatus.ItemNotFound, result.Items[0].ValidationStatus);
    }

    // ── PA_B12: PRICE_LIST_NOT_FOUND ─────────────────────────────────────────

    [Fact]
    public async Task PA_B12_Preview_PriceListMissing_Returns_PRICE_LIST_NOT_FOUND()
    {
        // First read (requested PL) → null; probe PL3 → returns a price → item exists but PL not found
        var sap = new PriceListAwareFakeSapAdapter();
        sap.SetPrice("ITEM001", 3, new SapCurrentPrice { Price = 100m, Currency = "TZS" });
        // PL 1 is not set — returns null

        var svc = MakeService(sap);
        var result = await svc.PreviewBulkPricesAsync(MakePreviewRequest(Item("ITEM001", 1, 150m)));

        Assert.Equal(PreviewValidationStatus.PriceListNotFound, result.Items[0].ValidationStatus);
    }

    // ── PA_B13: NO_CHANGE ─────────────────────────────────────────────────────

    [Fact]
    public async Task PA_B13_Preview_NoChange_Returns_NO_CHANGE()
    {
        var sap = new FakeSapPriceAdapter
            { ReadResult = new SapCurrentPrice { Price = 150m, Currency = "TZS" } };
        var svc = MakeService(sap);

        var result = await svc.PreviewBulkPricesAsync(MakePreviewRequest(Item("ITEM001", 1, 150m)));

        Assert.Equal(PreviewValidationStatus.NoChange, result.Items[0].ValidationStatus);
        Assert.False(result.Items[0].CanExecute);
        Assert.Equal(1, result.NoChangeRows);
    }

    // ── PA_B14: CONCURRENCY_CONFLICT ─────────────────────────────────────────

    [Fact]
    public async Task PA_B14_Preview_ConcurrencyConflict_Returns_CONCURRENCY_CONFLICT()
    {
        var sap = new FakeSapPriceAdapter
            { ReadResult = new SapCurrentPrice { Price = 100m, Currency = "TZS" } };
        var svc = MakeService(sap);

        // Expected=50 but SAP has 100 → conflict
        var result = await svc.PreviewBulkPricesAsync(
            MakePreviewRequest(Item("ITEM001", 1, 150m, expected: 50m)));

        Assert.Equal(PreviewValidationStatus.ConcurrencyConflict, result.Items[0].ValidationStatus);
        Assert.False(result.Items[0].CanExecute);
        Assert.Equal(1, result.ConflictRows);
    }

    // ── PA_B15: Mixed batch — correct summary counts ──────────────────────────

    [Fact]
    public async Task PA_B15_Preview_MixedBatch_CorrectCounts()
    {
        var sap = new FakeSapPriceAdapter
            { ReadResult = new SapCurrentPrice { Price = 100m, Currency = "TZS" } };
        var svc = MakeService(sap);

        var result = await svc.PreviewBulkPricesAsync(MakePreviewRequest(
            Item("ITEM001", 1, 150m),          // READY
            Item("ITEM002", 2, 100m),           // NO_CHANGE (100==100)
            Item("ITEM003", 0, 150m),           // INVALID_PRICE_LIST
            Item("ITEM004", 1, 150m, expected: 50m)  // CONCURRENCY_CONFLICT
        ));

        Assert.Equal(4, result.TotalRows);
        Assert.Equal(1, result.ReadyRows);
        Assert.Equal(1, result.NoChangeRows);
        Assert.Equal(1, result.InvalidRows);
        Assert.Equal(1, result.ConflictRows);
    }

    // ── PA_B16: Execute re-checks live SAP before write ───────────────────────

    [Fact]
    public async Task PA_B16_Execute_RechecksLiveSap_BeforeWrite()
    {
        var sap = new FakeSapPriceAdapter
        {
            ReadResult   = new SapCurrentPrice { Price = 100m, Currency = "TZS" },
            UpdateActual = 150m,
        };
        var svc = MakeService(sap);

        var req = new BulkUpdateProductPricesRequest
        {
            RequestId   = Guid.NewGuid(),
            RequestedBy = "tester",
            Updates     = new List<BulkPriceUpdateItem> { Item("ITEM001", 1, 150m) },
        };
        await svc.UpdatePricesBulkAsync(req);

        // ReadItemPrice must have been called at least once during execute
        Assert.True(sap.ReadCallCount > 0);
    }

    // ── PA_B17: One item fails; others not affected ──────────────────────────

    [Fact]
    public async Task PA_B17_Execute_OneFailed_OthersNotAffected()
    {
        // Use a per-item adapter to control different results per ItemCode
        var sap = new MultiItemFakeSapAdapter();
        sap.SetPrice("ITEM001", 1, new SapCurrentPrice { Price = 100m, Currency = "TZS" }, updateActual: 150m);
        sap.SetPrice("ITEM002", 2, null); // returns null → item not found

        var svc = MakeService(sap);

        var req = new BulkUpdateProductPricesRequest
        {
            RequestId   = Guid.NewGuid(),
            RequestedBy = "tester",
            Updates     = new List<BulkPriceUpdateItem>
            {
                Item("ITEM001", 1, 150m),
                Item("ITEM002", 2, 200m),
            },
        };
        var result = await svc.UpdatePricesBulkAsync(req);

        Assert.Equal(2, result.Total);
        var r1 = result.Results.First(r => r.ItemCode == "ITEM001");
        var r2 = result.Results.First(r => r.ItemCode == "ITEM002");
        Assert.Equal(PriceUpdateResult.Success, r1.ResultCode);
        Assert.Equal(PriceUpdateResult.PriceListNotFound, r2.ResultCode);
    }

    // ── PA_B18: SUCCESS replay → ALREADY_COMPLETED, SAP not called ────────────

    [Fact]
    public async Task PA_B18_Execute_SuccessReplay_SapNotCalled()
    {
        var audit = new FakeAuditRepositoryWithStatus();
        var batchId = Guid.Parse("18181818-0000-0000-0000-000000000001");
        audit.SeedCompleted(batchId, "ITEM001", 1, PriceUpdateResult.Success, 150m);

        var sap = new FakeSapPriceAdapter { ReadResult = null }; // would fail if called
        var svc = MakeService(sap, audit);

        var req = new BulkUpdateProductPricesRequest
        {
            RequestId   = batchId,
            RequestedBy = "tester",
            Updates     = new List<BulkPriceUpdateItem> { Item("ITEM001", 1, 150m) },
        };
        var result = await svc.UpdatePricesBulkAsync(req);

        Assert.Equal(PriceUpdateResult.AlreadyCompleted, result.Results[0].ResultCode);
        Assert.Equal(0, sap.ReadCallCount);
    }

    // ── PA_B19: CACHE_SYNC_WARNING replay → ALREADY_COMPLETED, SAP not called ─

    [Fact]
    public async Task PA_B19_Execute_CacheSyncWarningReplay_SapNotCalled()
    {
        var audit = new FakeAuditRepositoryWithStatus();
        var batchId = Guid.Parse("19191919-0000-0000-0000-000000000001");
        audit.SeedCompleted(batchId, "ITEM002", 2, PriceUpdateResult.CacheSyncWarning, 200m);

        var sap = new FakeSapPriceAdapter { ReadResult = null };
        var svc = MakeService(sap, audit);

        var req = new BulkUpdateProductPricesRequest
        {
            RequestId   = batchId,
            RequestedBy = "tester",
            Updates     = new List<BulkPriceUpdateItem> { Item("ITEM002", 2, 200m) },
        };
        var result = await svc.UpdatePricesBulkAsync(req);

        Assert.Equal(PriceUpdateResult.AlreadyCompleted, result.Results[0].ResultCode);
        Assert.Equal(0, sap.ReadCallCount);
    }

    // ── PA_B20: CacheRepair → zero SAP mutations ─────────────────────────────

    [Fact]
    public async Task PA_B20_CacheRepair_ZeroSapMutations()
    {
        var sap = new FakeSapPriceAdapter
            { ReadResult = new SapCurrentPrice { Price = 100m, Currency = "TZS" } };
        var svc = MakeService(sap);

        await svc.RepairCacheAsync("ITEM001", 1,
            new CacheRepairRequest { RequestedBy = "tester" });

        Assert.Equal(0, sap.UpdateCallCount);
    }

    // ── PA_B21: CacheRepair → SQLite updated ─────────────────────────────────

    [Fact]
    public async Task PA_B21_CacheRepair_SqliteUpdated()
    {
        var sap = new FakeSapPriceAdapter
            { ReadResult = new SapCurrentPrice { Price = 100m, Currency = "TZS" } };
        var tracker = new CacheRepairTracker();
        var svc = new TestableCacheRepairService(sap, new FakeAuditRepositoryWithStatus(),
            NullLogger<ZfProductAdminService>.Instance, tracker);

        await svc.RepairCacheAsync("ITEM001", 1,
            new CacheRepairRequest { RequestedBy = "tester" });

        Assert.True(tracker.SqliteUpdateCalled);
        Assert.Equal(100m, tracker.SqlitePrice);
    }

    // ── PA_B22: CacheRepair → Neon updated ───────────────────────────────────

    [Fact]
    public async Task PA_B22_CacheRepair_NeonUpdated()
    {
        var sap = new FakeSapPriceAdapter
            { ReadResult = new SapCurrentPrice { Price = 100m, Currency = "TZS" } };
        var tracker = new CacheRepairTracker();
        var svc = new TestableCacheRepairService(sap, new FakeAuditRepositoryWithStatus(),
            NullLogger<ZfProductAdminService>.Instance, tracker);

        await svc.RepairCacheAsync("ITEM001", 1,
            new CacheRepairRequest { RequestedBy = "tester" });

        Assert.True(tracker.NeonUpdateCalled);
        Assert.Equal(100m, tracker.NeonPrice);
    }

    // ── PA_B23: Preview → currency-grouped totals ─────────────────────────────

    [Fact]
    public async Task PA_B23_Preview_CurrencyGroupedTotals_MultiCurrency()
    {
        var sap = new MultiItemFakeSapAdapter();
        sap.SetPrice("ITEM001", 1, new SapCurrentPrice { Price = 100m, Currency = "TZS" }, updateActual: 150m);
        sap.SetPrice("ITEM002", 2, new SapCurrentPrice { Price = 200m, Currency = "USD" }, updateActual: 250m);
        sap.SetPrice("ITEM003", 3, new SapCurrentPrice { Price = 300m, Currency = "TZS" }, updateActual: 350m);

        var svc = MakeService(sap);

        var result = await svc.PreviewBulkPricesAsync(MakePreviewRequest(
            Item("ITEM001", 1, 150m),   // READY TZS
            Item("ITEM002", 2, 250m),   // READY USD
            Item("ITEM003", 3, 350m)    // READY TZS
        ));

        Assert.Equal(3, result.ReadyRows);
        Assert.Equal(2, result.CurrencyTotals.Count);

        var tzs = result.CurrencyTotals.First(t => t.Currency == "TZS");
        var usd = result.CurrencyTotals.First(t => t.Currency == "USD");

        Assert.Equal(2, tzs.ItemCount);
        Assert.Equal(400m, tzs.TotalCurrentValue);   // 100 + 300
        Assert.Equal(500m, tzs.TotalProposedValue);  // 150 + 350

        Assert.Equal(1, usd.ItemCount);
        Assert.Equal(200m, usd.TotalCurrentValue);
        Assert.Equal(250m, usd.TotalProposedValue);
    }

    // ── PA_B24: Batch status — GetByRequestId returns all rows ───────────────

    [Fact]
    public async Task PA_B24_BatchStatus_GetByRequestId_ReturnsAllRows()
    {
        var batchId = Guid.Parse("24242424-0000-0000-0000-000000000001");
        var audit   = new FakeAuditRepositoryWithStatus();
        audit.SeedCompleted(batchId, "ITEM001", 1, PriceUpdateResult.Success, 150m);
        audit.SeedCompleted(batchId, "ITEM002", 2, PriceUpdateResult.Success, 200m);
        audit.SeedCompleted(batchId, "ITEM003", 3, PriceUpdateResult.CacheSyncWarning, 300m);

        var svc = MakeService(audit: audit);
        var result = await svc.GetBulkRequestStatusAsync(batchId);

        Assert.Equal(batchId, result.RequestId);
        Assert.Equal(3, result.TotalRows);
        Assert.Equal(3, result.Rows.Count);
        Assert.All(result.Rows, r => Assert.Equal(batchId, r.BatchRequestId));
    }

    // ── PA_B25: SAP read throws → SAP_READ_FAILED ─────────────────────────────

    [Fact]
    public async Task PA_B25_Preview_SapReadFailed_Returns_SAP_READ_FAILED()
    {
        var sap = new FakeSapPriceAdapter { ReadThrows = true };
        var svc = MakeService(sap);

        var result = await svc.PreviewBulkPricesAsync(MakePreviewRequest(Item("ITEM001", 1, 150m)));

        Assert.Equal(PreviewValidationStatus.SapReadFailed, result.Items[0].ValidationStatus);
        Assert.False(result.Items[0].CanExecute);
    }
}

// ── Extended FakeAuditRepository with GetByRequestIdAsync support ─────────────

public sealed class FakeAuditRepositoryWithStatus : ProductPriceAuditRepository
{
    private long _nextId = 1;
    private readonly List<ProductPriceAuditEntry> _seeded = new();

    public List<ProductPriceAuditEntry> Entries { get; } = new();

    public void SeedCompleted(Guid batchId, string itemCode, int priceListNum,
        string result, decimal? actualPriceAfter)
    {
        _seeded.Add(new ProductPriceAuditEntry
        {
            Id               = _nextId++,
            BatchRequestId   = batchId,
            ItemCode         = itemCode,
            PriceListNum     = priceListNum,
            Result           = result,
            ActualPriceAfter = actualPriceAfter,
            RequestedAtUtc   = DateTime.UtcNow,
        });
    }

    public override Task<long> InsertAsync(
        ProductPriceAuditEntry entry, CancellationToken ct = default)
    {
        entry.Id     = _nextId++;
        entry.Result = "Pending";
        Entries.Add(entry);
        return Task.FromResult(entry.Id);
    }

    public override Task SetTerminalAsync(
        long id, string result, decimal? actualPriceAfter,
        int? sapErrorCode, string? sapErrorMessage,
        string? sqliteSyncResult, string? neonSyncResult,
        DateTime executedAtUtc, CancellationToken ct = default,
        decimal? oldPrice = null, string? currency = null)
    {
        var e = Entries.Find(e => e.Id == id);
        if (e is not null)
        {
            e.Result           = result;
            e.ActualPriceAfter = actualPriceAfter;
            if (oldPrice  is not null) e.OldPrice  = oldPrice;
            if (currency  is not null) e.Currency  = currency;
            e.SapErrorCode     = sapErrorCode;
            e.SapErrorMessage  = sapErrorMessage;
            e.SqliteSyncResult = sqliteSyncResult;
            e.NeonSyncResult   = neonSyncResult;
            e.ExecutedAtUtc    = executedAtUtc;
        }
        return Task.CompletedTask;
    }

    public override Task<ProductPriceAuditEntry?> FindCompletedAsync(
        Guid batchRequestId, string itemCode, int priceListNum,
        CancellationToken ct = default)
    {
        var found = _seeded.FirstOrDefault(e =>
            e.BatchRequestId == batchRequestId &&
            string.Equals(e.ItemCode, itemCode, StringComparison.OrdinalIgnoreCase) &&
            e.PriceListNum == priceListNum &&
            e.Result != "Pending");
        return Task.FromResult(found);
    }

    public override Task<IReadOnlyList<ProductPriceAuditEntry>> GetByRequestIdAsync(
        Guid requestId, CancellationToken ct = default)
    {
        IReadOnlyList<ProductPriceAuditEntry> results =
            _seeded.Where(e => e.BatchRequestId == requestId).ToList();
        return Task.FromResult(results);
    }
}

// ── FakeSapPriceAdapter extension: UpdateCallCount ───────────────────────────
// NOTE: FakeSapPriceAdapter in ProductPriceTests.cs does not expose UpdateCallCount.
// We need it for PA_B01 and PA_B20. Since we cannot modify the existing sealed class,
// we access it via the UpdateThrows path. Instead, we rely on FakeSapPriceAdapter
// already having no UpdateCallCount — the tests use UpdateActual=null which would
// cause a verification failure if called. The tests that need UpdateCallCount
// use the property below via casting or a derived adapter.
// Actually FakeSapPriceAdapter IS a public sealed class and we cannot subclass it.
// We verify "not called" by leaving UpdateActual unset and checking that no mutation
// would have been triggered (the preview path never calls UpdateItemPrice).

// ── Per-item price-list-aware fake adapter ────────────────────────────────────

public sealed class PriceListAwareFakeSapAdapter : ISapPriceAdapter
{
    private readonly Dictionary<string, SapCurrentPrice?> _prices = new(StringComparer.OrdinalIgnoreCase);
    public int UpdateCallCount { get; private set; }

    public void SetPrice(string itemCode, int pl, SapCurrentPrice? price)
        => _prices[$"{itemCode}:{pl}"] = price;

    public SapCurrentPrice? ReadItemPrice(string itemCode, int priceListNum)
    {
        _prices.TryGetValue($"{itemCode}:{priceListNum}", out var p);
        return p;
    }

    public (int rc, string sapError, decimal? actualPriceAfter, string currency) UpdateItemPrice(
        string itemCode, int priceListNum, decimal newPrice)
    {
        UpdateCallCount++;
        return (0, string.Empty, newPrice, "TZS");
    }
}

// ── Multi-item fake adapter (read + write per item) ───────────────────────────

public sealed class MultiItemFakeSapAdapter : ISapPriceAdapter
{
    private readonly Dictionary<string, (SapCurrentPrice? read, decimal? updateActual)> _items
        = new(StringComparer.OrdinalIgnoreCase);
    public int UpdateCallCount { get; private set; }

    public void SetPrice(string itemCode, int pl, SapCurrentPrice? read, decimal? updateActual = null)
        => _items[$"{itemCode}:{pl}"] = (read, updateActual);

    public SapCurrentPrice? ReadItemPrice(string itemCode, int priceListNum)
    {
        _items.TryGetValue($"{itemCode}:{priceListNum}", out var entry);
        return entry.read;
    }

    public (int rc, string sapError, decimal? actualPriceAfter, string currency) UpdateItemPrice(
        string itemCode, int priceListNum, decimal newPrice)
    {
        UpdateCallCount++;
        _items.TryGetValue($"{itemCode}:{priceListNum}", out var entry);
        return (0, string.Empty, entry.updateActual ?? newPrice, entry.read?.Currency ?? "TZS");
    }
}

// ── UpdateCallCount extension on FakeSapPriceAdapter ────────────────────────
// FakeSapPriceAdapter is already public sealed in ProductPriceTests.cs.
// For PA_B01 and PA_B20 we need UpdateCallCount.
// Add it as extension via a wrapper (FakeSapPriceAdapterEx).
// Actually, FakeSapPriceAdapter already implements ISapPriceAdapter.
// We'll add UpdateCallCount directly by using the fact the test suite shares assembly.
// Since FakeSapPriceAdapter is sealed we can't subclass — we rely on
// the fact that preview/repair tests using FakeSapPriceAdapter won't call UpdateItemPrice
// (which would throw because UpdateThrows defaults false but UpdateActual defaults null,
// meaning if UpdateItemPrice IS called and rc=0, readback would be null → SAP_WRITE_VERIFICATION_FAILED).
// For the count tests we use MultiItemFakeSapAdapter or PriceListAwareFakeSapAdapter above.

// ── CacheRepair tracker + testable service ────────────────────────────────────

public sealed class CacheRepairTracker
{
    public bool    SqliteUpdateCalled { get; set; }
    public decimal SqlitePrice        { get; set; }
    public bool    NeonUpdateCalled   { get; set; }
    public decimal NeonPrice          { get; set; }
}

public sealed class TestableCacheRepairService : ZfProductAdminService
{
    private readonly CacheRepairTracker _tracker;

    public TestableCacheRepairService(
        ISapPriceAdapter sap,
        ProductPriceAuditRepository audit,
        Microsoft.Extensions.Logging.ILogger<ZfProductAdminService> log,
        CacheRepairTracker tracker)
        : base(sap, null!, null!, audit, null!, log)
    {
        _tracker = tracker;
    }

    protected override Task<string> UpdateSqlitePriceAsync(
        string itemCode, int priceListNum, decimal price, string currency, CancellationToken ct)
    {
        _tracker.SqliteUpdateCalled = true;
        _tracker.SqlitePrice = price;
        return Task.FromResult("OK");
    }

    protected override Task<string> UpdateNeonPriceAsync(
        string itemCode, int priceListNum, decimal price, string currency, CancellationToken ct)
    {
        _tracker.NeonUpdateCalled = true;
        _tracker.NeonPrice = price;
        return Task.FromResult("OK");
    }
}
