using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SapReplitAPI.Models.Cache;
using SapReplitAPI.Models.Warehouse;
using SapReplitAPI.Models.ZoneFulfillment;
using SapReplitAPI.Services.PickList;
using SapReplitAPI.Services.Warehouse;
using Xunit;

namespace SapReplitAPI.Tests.Warehouse;

/// <summary>
/// WB01–WB14: Warehouse App "Select Bin &amp; Pick" workflow.
///
/// WB01–WB03: PickListUiStateComputer — state detection from cache + candidates.
/// WB04–WB08: PickBinValidator — bin selection validation (pure static).
/// WB09–WB11: WarehouseBinPickService — confirm-pick service behavior.
/// WB12–WB14: Safety gates — no Tiered realloc, no ODLN, existing states preserved.
///
/// No SAP COM calls. All OIBQ data via IWarehousePickSapAdapter fake.
/// No ZF services referenced. SQLite in-memory for cache.
/// </summary>
public sealed class WarehouseBinPickTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly CacheDbContext   _db;
    private readonly PickListCacheService _cache;

    public WarehouseBinPickTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();

        var opts = new DbContextOptionsBuilder<CacheDbContext>()
            .UseSqlite(_conn)
            .Options;
        _db = new CacheDbContext(opts);
        _db.Database.EnsureCreated();

        // SapService and PickListTimestampReader not used by cache read methods — null is safe.
        _cache = new PickListCacheService(null!, _db, null!, NullLogger<PickListCacheService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // WB01–WB03: State detection
    // ─────────────────────────────────────────────────────────────────────────

    // WB01 — Released + bins=0 + stock available → AwaitingBinSelection
    [Fact]
    public void WB01_Released_NoBins_StockAvailable_AwaitingBinSelection()
    {
        var state = PickListUiStateComputer.Compute(
            sapStatus: "R", canceled: "N", allocatedBinCount: 0, liveCandidateCount: 3);

        Assert.Equal(PickListUiState.AwaitingBinSelection, state);
        Assert.Equal("Select Bin & Pick", PickListUiStateComputer.ToAction(state));
        Assert.Equal("Ready to Pick",     PickListUiStateComputer.ToLabel(state));
    }

    // WB02 — Released + bins=0 + no OIBQ stock → NoStock
    [Fact]
    public void WB02_Released_NoBins_NoStock_NoStock()
    {
        var state = PickListUiStateComputer.Compute(
            sapStatus: "R", canceled: "N", allocatedBinCount: 0, liveCandidateCount: 0);

        Assert.Equal(PickListUiState.NoStock, state);
        Assert.Equal(string.Empty, PickListUiStateComputer.ToAction(state));
        Assert.Equal("No Stock in Bins", PickListUiStateComputer.ToLabel(state));
        Assert.Contains("No stock found", PickListUiStateComputer.ToSupportingText(state));
    }

    // WB03 — Released + bins>0 → ReadyAllocated (existing allocations, no candidates needed)
    [Fact]
    public void WB03_Released_BinsAllocated_ReadyAllocated()
    {
        var state = PickListUiStateComputer.Compute(
            sapStatus: "R", canceled: "N", allocatedBinCount: 2, liveCandidateCount: -1);

        Assert.Equal(PickListUiState.ReadyAllocated, state);
        Assert.Equal("Confirm Pick", PickListUiStateComputer.ToAction(state));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // WB04–WB08: Bin selection validation
    // ─────────────────────────────────────────────────────────────────────────

    // WB04 — wrong-warehouse bin (not in candidates list) → rejected
    [Fact]
    public void WB04_WrongWarehouseBin_Rejected()
    {
        // Candidates contain only WHS=003 bins; selection uses a bin (BinAbs=99) not in the list.
        var candidates = new[]
        {
            new BinCandidateDto(BinAbsEntry: 588, BinCode: "003-RACK-A", WhsCode: "003", AvailableQty: 11m),
        };
        var selections = new[]
        {
            new BinAllocationRequestDto { BinAbsEntry = 99, Qty = 1m }, // bin 99 not in candidates
        };

        var err = PickBinValidator.ValidateLine(1m, candidates, selections);

        Assert.NotNull(err);
        Assert.Contains("not a valid candidate", err);
    }

    // WB05 — disabled bin (absent from candidates since candidates filter Disabled='N') → rejected
    [Fact]
    public void WB05_DisabledBin_Rejected()
    {
        // Disabled bins do not appear in the candidate list returned by QueryWarehouseBinCandidates.
        // Selecting a bin absent from candidates is therefore equivalent to selecting a disabled one.
        var candidates = new[]
        {
            new BinCandidateDto(BinAbsEntry: 588, BinCode: "003-RACK-A", WhsCode: "003", AvailableQty: 5m),
        };
        var selections = new[]
        {
            new BinAllocationRequestDto { BinAbsEntry = 777, Qty = 1m }, // 777 = disabled bin, not in list
        };

        var err = PickBinValidator.ValidateLine(1m, candidates, selections);

        Assert.NotNull(err);
    }

    // WB06 — bin qty requested > available → rejected
    [Fact]
    public void WB06_InsufficientBinQty_Rejected()
    {
        var candidates = new[]
        {
            new BinCandidateDto(BinAbsEntry: 284, BinCode: "002-SHELF-A", WhsCode: "002", AvailableQty: 1m),
        };
        var selections = new[]
        {
            new BinAllocationRequestDto { BinAbsEntry = 284, Qty = 2m }, // requesting 2 but only 1 available
        };

        var err = PickBinValidator.ValidateLine(2m, candidates, selections);

        Assert.NotNull(err);
        Assert.Contains("only 1", err);
    }

    // WB07 — multi-bin selection totals correctly
    [Fact]
    public void WB07_MultiBin_TotalsCorrectly()
    {
        var candidates = new[]
        {
            new BinCandidateDto(BinAbsEntry: 1, BinCode: "003-A", WhsCode: "003", AvailableQty: 2m),
            new BinCandidateDto(BinAbsEntry: 2, BinCode: "003-B", WhsCode: "003", AvailableQty: 1m),
        };
        var selections = new[]
        {
            new BinAllocationRequestDto { BinAbsEntry = 1, Qty = 2m },
            new BinAllocationRequestDto { BinAbsEntry = 2, Qty = 1m },
        };

        var err = PickBinValidator.ValidateLine(3m, candidates, selections);

        Assert.Null(err); // total=3 equals desiredPickedQty=3 — valid
    }

    // WB08 — partial pick (selected < releasedQty) is valid if total == desiredPickedQty
    [Fact]
    public void WB08_PartialPick_Accepted()
    {
        // ReleasedQty=5, picker physically found only 3 — submits desiredPickedQty=3.
        var candidates = new[]
        {
            new BinCandidateDto(BinAbsEntry: 1, BinCode: "003-A", WhsCode: "003", AvailableQty: 5m),
        };
        var selections = new[]
        {
            new BinAllocationRequestDto { BinAbsEntry = 1, Qty = 3m },
        };

        var err = PickBinValidator.ValidateLine(3m, candidates, selections); // desiredPickedQty=3, not RelQtty=5

        Assert.Null(err); // partial pick is explicitly supported
    }

    // ─────────────────────────────────────────────────────────────────────────
    // WB09–WB11: Service-level confirm-pick behavior
    // ─────────────────────────────────────────────────────────────────────────

    // WB09 — successful SAP confirmation triggers targeted AbsEntry cache refresh
    [Fact]
    public async Task WB09_SuccessfulConfirm_TriggersRefresh()
    {
        SeedPickList(absEntry: 500, status: "R");
        SeedPickListLine(absEntry: 500, pickEntry: 1, itemCode: "ITM001", whsCode: "003", relQtty: 1m);

        var sapAdapter = new FakeWarehousePickSapAdapter();
        sapAdapter.CandidatesToReturn = new List<BinCandidateDto>
        {
            new(BinAbsEntry: 100, BinCode: "003-A", WhsCode: "003", AvailableQty: 5m),
        };
        sapAdapter.PickResult = (0, null, new Pkl1LineState(500, 100, 0, 17, 1m, 1m, "Y"));

        var refreshFake = new FakePickListRefreshService();

        var svc = BuildService(sapAdapter, refreshFake);

        var req = new ConfirmPickRequest
        {
            Lines = new List<ConfirmPickLineDto>
            {
                new ConfirmPickLineDto
                {
                    PickEntry = 1,
                    PickedQty = 1m,
                    Bins = new List<BinAllocationRequestDto>
                    {
                        new BinAllocationRequestDto { BinAbsEntry = 100, Qty = 1m },
                    },
                },
            },
        };

        var result = await svc.ConfirmPickAsync(500, req);

        Assert.True(result.Success);
        Assert.Single(result.Lines);
        Assert.True(result.Lines[0].Success);
        // Refresh must have been triggered exactly once for this AbsEntry
        Assert.Equal(1, refreshFake.RefreshCount);
        Assert.Equal(500, refreshFake.LastRefreshedAbsEntry);
    }

    // WB10 — failed SAP confirmation does NOT fake a Picked state
    [Fact]
    public async Task WB10_FailedSap_DoesNotFakePickedState()
    {
        SeedPickList(absEntry: 501, status: "R");
        SeedPickListLine(absEntry: 501, pickEntry: 1, itemCode: "ITM002", whsCode: "002", relQtty: 1m);

        var sapAdapter = new FakeWarehousePickSapAdapter();
        sapAdapter.CandidatesToReturn = new List<BinCandidateDto>
        {
            new(BinAbsEntry: 200, BinCode: "002-A", WhsCode: "002", AvailableQty: 3m),
        };
        sapAdapter.PickResult = (-108, "Bin not available", null); // SAP failure

        var refreshFake = new FakePickListRefreshService();
        var svc = BuildService(sapAdapter, refreshFake);

        var req = new ConfirmPickRequest
        {
            Lines = new List<ConfirmPickLineDto>
            {
                new ConfirmPickLineDto
                {
                    PickEntry = 1,
                    PickedQty = 1m,
                    Bins = new List<BinAllocationRequestDto>
                    {
                        new BinAllocationRequestDto { BinAbsEntry = 200, Qty = 1m },
                    },
                },
            },
        };

        var result = await svc.ConfirmPickAsync(501, req);

        Assert.False(result.Success);
        Assert.Single(result.Lines);
        Assert.False(result.Lines[0].Success);
        Assert.Equal(-108, result.Lines[0].SapRc);
        // Refresh must NOT be triggered when SAP failed
        Assert.Equal(0, refreshFake.RefreshCount);
    }

    // WB11 — refresh is scoped to the AbsEntry being confirmed only
    [Fact]
    public async Task WB11_Refresh_ScopedToConfirmedAbsEntry()
    {
        // Seed two pick lists; confirm only one → refresh must only fire for the confirmed one.
        SeedPickList(absEntry: 600, status: "R");
        SeedPickList(absEntry: 601, status: "R");
        SeedPickListLine(absEntry: 600, pickEntry: 1, itemCode: "ITM003", whsCode: "003", relQtty: 1m);
        SeedPickListLine(absEntry: 601, pickEntry: 2, itemCode: "ITM004", whsCode: "003", relQtty: 1m);

        var sapAdapter = new FakeWarehousePickSapAdapter();
        sapAdapter.CandidatesToReturn = new List<BinCandidateDto>
        {
            new(BinAbsEntry: 300, BinCode: "003-A", WhsCode: "003", AvailableQty: 5m),
        };
        sapAdapter.PickResult = (0, null, new Pkl1LineState(600, 100, 0, 17, 1m, 1m, "Y"));

        var refreshFake = new FakePickListRefreshService();
        var svc = BuildService(sapAdapter, refreshFake);

        var req = new ConfirmPickRequest
        {
            Lines = new List<ConfirmPickLineDto>
            {
                new ConfirmPickLineDto
                {
                    PickEntry = 1,
                    PickedQty = 1m,
                    Bins = new List<BinAllocationRequestDto>
                    {
                        new BinAllocationRequestDto { BinAbsEntry = 300, Qty = 1m },
                    },
                },
            },
        };

        await svc.ConfirmPickAsync(600, req);

        Assert.Equal(600, refreshFake.LastRefreshedAbsEntry); // only 600 was refreshed
        Assert.Equal(1,   refreshFake.RefreshCount);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // WB12–WB14: Safety gates
    // ─────────────────────────────────────────────────────────────────────────

    // WB12 — no Tiered reallocation triggered by WarehouseBinPickService
    [Fact]
    public void WB12_NoTieredReallocationInService()
    {
        // WarehouseBinPickService has no dependency on ZoneAllocationEngine,
        // TieredZoneAllocationEngine, or ZoneFulfillmentOrchestrationService.
        // This test proves the service's constructor signature doesn't reference them.
        var serviceType = typeof(WarehouseBinPickService);
        var constructors = serviceType.GetConstructors();
        Assert.Single(constructors);
        var parameters = constructors[0].GetParameters();
        var paramNames = parameters.Select(p => p.ParameterType.Name).ToArray();
        Assert.DoesNotContain("ZoneAllocationEngine",          paramNames);
        Assert.DoesNotContain("TieredZoneAllocationEngine",    paramNames);
        Assert.DoesNotContain("ZoneFulfillmentOrchestrationService", paramNames);
    }

    // WB13 — no ODLN or OINV created by WarehouseBinPickService
    [Fact]
    public void WB13_NoDeliveryOrInvoiceServiceInWarehouseService()
    {
        // WarehouseBinPickService must not depend on delivery or invoice services.
        var serviceType = typeof(WarehouseBinPickService);
        var constructors = serviceType.GetConstructors();
        var parameters = constructors[0].GetParameters();
        var paramNames = parameters.Select(p => p.ParameterType.Name).ToArray();
        Assert.DoesNotContain("ZoneFulfillmentDeliveryService", paramNames);
        Assert.DoesNotContain("ZoneFulfillmentInvoiceService",  paramNames);
        Assert.DoesNotContain("SoDeliveryService",              paramNames);
    }

    // WB14 — existing Y/P/D/C status states produce correct labels (display behavior unchanged)
    [Theory]
    [InlineData("Y", "N", 0, -1, "Picked")]
    [InlineData("P", "N", 0, -1, "PartiallyPicked")]
    [InlineData("D", "N", 0, -1, "PartiallyDelivered")]
    [InlineData("C", "N", 0, -1, "Closed")]
    [InlineData("R", "Y", 0, -1, "Closed")]  // Canceled=Y overrides status
    public void WB14_ExistingStatuses_DisplayCorrectly(
        string sapStatus, string canceled, int bins, int candidates, string expectedState)
    {
        var state = PickListUiStateComputer.Compute(sapStatus, canceled, bins, candidates);
        Assert.Equal(expectedState, state.ToString());
        // No "Select Bin & Pick" action for terminal states
        Assert.Equal(string.Empty, PickListUiStateComputer.ToAction(state));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    private WarehouseBinPickService BuildService(
        IWarehousePickSapAdapter sap,
        IPickListEventRefreshService refresh)
        => new WarehouseBinPickService(
            sap, _cache, refresh, NullLogger<WarehouseBinPickService>.Instance);

    private void SeedPickList(int absEntry, string status, string canceled = "N")
    {
        _db.PickLists.Add(new CachedPickList
        {
            AbsEntry   = absEntry,
            Status     = status,
            Canceled   = canceled,
            Name       = $"PL-{absEntry}",
            OwnerCode  = 1,
            OwnerName  = "Test",
            SlpName    = string.Empty,
            Remarks    = string.Empty,
            PickDate   = DateTime.Today,
            CreateDate = DateTime.Today,
            UpdateDate = DateTime.Today,
            LastSyncedAt = DateTime.UtcNow,
        });
        _db.SaveChanges();
        _db.ChangeTracker.Clear();
    }

    private void SeedPickListLine(
        int absEntry, int pickEntry, string itemCode, string whsCode, decimal relQtty)
    {
        _db.PickListLines.Add(new CachedPickListLine
        {
            AbsEntry   = absEntry,
            PickEntry  = pickEntry,
            OrderEntry = absEntry * 100,
            OrderLine  = 0,
            BaseObject = 17,
            RelQtty    = relQtty,
            PickQtty   = 0m,
            PickStatus = "R",
            PrevReleas = relQtty,
            ItemCode   = itemCode,
            Dscription = $"Test {itemCode}",
            WhsCode    = whsCode,
        });
        _db.SaveChanges();
        _db.ChangeTracker.Clear();
    }
}

// ─── Fakes ────────────────────────────────────────────────────────────────────

/// <summary>Deterministic fake for IWarehousePickSapAdapter — no SAP COM calls.</summary>
internal sealed class FakeWarehousePickSapAdapter : IWarehousePickSapAdapter
{
    public List<BinCandidateDto> CandidatesToReturn { get; set; } = new();
    public (int Rc, string? SapError, Pkl1LineState? PostState) PickResult { get; set; }
        = (0, null, null);

    public List<BinCandidateDto> QueryBinCandidates(string itemCode, string whsCode)
        => CandidatesToReturn;

    public (int Rc, string? SapError, Pkl1LineState? PostState) ExecutePick(
        int absEntry, int soDocEntry, int soLineNum,
        double desiredPickedQty, IReadOnlyList<BinPickAlloc> binAllocs,
        string itemCode, string whsCode)
        => PickResult;
}

/// <summary>Fake refresh service that records calls without hitting SAP or cache.</summary>
internal sealed class FakePickListRefreshService : IPickListEventRefreshService
{
    public int RefreshCount             { get; private set; }
    public int LastRefreshedAbsEntry    { get; private set; }

    public Task<(bool ok, string? error)> RefreshAsync(
        int absEntry, CancellationToken ct)
    {
        RefreshCount++;
        LastRefreshedAbsEntry = absEntry;
        return Task.FromResult((ok: true, error: (string?)null));
    }
}
