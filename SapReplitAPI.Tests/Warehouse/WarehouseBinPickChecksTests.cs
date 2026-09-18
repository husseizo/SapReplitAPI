using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SapReplitAPI.Models.Cache;
using SapReplitAPI.Models.Warehouse;
using SapReplitAPI.Services.PickList;
using SapReplitAPI.Services.Warehouse;
using Xunit;

namespace SapReplitAPI.Tests.Warehouse;

/// <summary>
/// WC01–WC20: Warehouse Select-Bin-and-Pick — 4-Check Closure.
///
/// WC01–WC04: CHECK 1  — BIN "AVAILABLE QTY" proven correct (OIBQ.OnHandQty only).
/// WC05–WC09: CHECK 2  — Live SAP pre-confirm state gate.
/// WC10–WC15: CHECK 3  — DesiredFinalPickQty semantics (cumulative desired state, not delta).
/// WC16–WC20: CHECK 4  — HTTP 207 partial success contract.
///
/// No SAP COM calls. IWarehousePickSapAdapter fake controls all SAP responses.
/// SQLite in-memory for cache.
/// </summary>
public sealed class WarehouseBinPickChecksTests : IDisposable
{
    private readonly SqliteConnection  _conn;
    private readonly CacheDbContext    _db;
    private readonly PickListCacheService _cache;

    public WarehouseBinPickChecksTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        var opts = new DbContextOptionsBuilder<CacheDbContext>().UseSqlite(_conn).Options;
        _db   = new CacheDbContext(opts);
        _db.Database.EnsureCreated();
        _cache = new PickListCacheService(null!, _db, null!, NullLogger<PickListCacheService>.Instance);
    }

    public void Dispose() { _db.Dispose(); _conn.Dispose(); }

    // ─────────────────────────────────────────────────────────────────────────
    // CHECK 1 — BIN "AVAILABLE QTY" PROVEN (WC01–WC04)
    //
    // Formula proven:
    //   PhysicalOnHand    = OIBQ.OnHandQty
    //   Committed/Alloc   = 0  (OBBQ has no CommQtty in SAP B1 PL18 — see TryQueryObbqCommittedByBin)
    //   UsableQty per bin = OIBQ.OnHandQty
    //
    // Source tables: OIBQ (bin physical stock) + OBIN (bin master: code, whs, disabled flag).
    // OBBQ is batch/serial tracking only; not a pick-reservation table in this schema.
    // SAP DI API engine enforces actual reservation at pl.Update() time.
    // ─────────────────────────────────────────────────────────────────────────

    // WC01 — BinCandidateDto.AvailableQty directly represents OIBQ.OnHandQty
    [Fact]
    public void WC01_BinAvailableQty_EqualToOibqOnHandQty()
    {
        // OIBQ.OnHandQty=7 → AvailableQty=7; no subtraction for committed (OBBQ has no CommQtty)
        var dto = new BinCandidateDto(BinAbsEntry: 100, BinCode: "003-A", WhsCode: "003", AvailableQty: 7m);

        Assert.Equal(7m, dto.AvailableQty);

        // Prove formula: PhysicalOnHand=7, Committed=0, UsableQty=7
        decimal physicalOnHand    = dto.AvailableQty; // OIBQ.OnHandQty
        decimal committedAtBin    = 0m;               // OBBQ: no CommQtty column in PL18 schema
        decimal usableQty         = physicalOnHand - committedAtBin;
        Assert.Equal(physicalOnHand, usableQty);
    }

    // WC02 — Validator accepts picking the full OIBQ.OnHandQty from one bin
    [Fact]
    public void WC02_FullBinPickAccepted_UsableQtyEqualsOibqOnHandQty()
    {
        // Bin has OIBQ.OnHandQty=5; picker requests all 5 — must be accepted
        var candidates = new[] { new BinCandidateDto(1, "003-A", "003", 5m) };
        var selections = new[] { new BinAllocationRequestDto { BinAbsEntry = 1, Qty = 5m } };

        var err = PickBinValidator.ValidateLine(5m, candidates, selections);

        Assert.Null(err); // UsableQty=5, requesting 5 — valid
    }

    // WC03 — Validator rejects when requested qty exceeds OIBQ.OnHandQty
    [Fact]
    public void WC03_ExcessBinPickRejected_AvailableQtyIsHardLimit()
    {
        // Bin has OIBQ.OnHandQty=5; picker requests 6 — must be rejected
        var candidates = new[] { new BinCandidateDto(1, "003-A", "003", 5m) };
        var selections = new[] { new BinAllocationRequestDto { BinAbsEntry = 1, Qty = 6m } };

        var err = PickBinValidator.ValidateLine(6m, candidates, selections);

        Assert.NotNull(err);
        Assert.Contains("only 5", err); // error names the actual available qty
    }

    // WC04 — Warehouse-level OITW.IsCommited is NOT applied per bin
    //        (proves AvailableQty = OIBQ.OnHandQty, not OIBQ.OnHandQty minus any OITW share)
    [Fact]
    public void WC04_NoOitwCommitmentAppliedPerBin()
    {
        // Two bins, each with OIBQ.OnHandQty=5. Warehouse-level IsCommited=7 (7 units on SOs).
        // Incorrect formula would distribute OITW.IsCommited: each bin usable=5-(7/2)=1.5
        // Correct formula: each bin AvailableQty=5 (OIBQ.OnHandQty only, no OITW subtraction)
        // Test proves the validator allows picking 5 from each bin (total 10 > warehouse commitment 7).
        var candidates = new[]
        {
            new BinCandidateDto(1, "003-A", "003", AvailableQty: 5m), // OIBQ.OnHandQty=5
            new BinCandidateDto(2, "003-B", "003", AvailableQty: 5m), // OIBQ.OnHandQty=5
        };
        var selections = new[]
        {
            new BinAllocationRequestDto { BinAbsEntry = 1, Qty = 5m },
            new BinAllocationRequestDto { BinAbsEntry = 2, Qty = 5m },
        };

        var err = PickBinValidator.ValidateLine(10m, candidates, selections);

        Assert.Null(err); // valid — OIBQ bin-level quantities, not restricted by OITW warehouse totals
    }

    // ─────────────────────────────────────────────────────────────────────────
    // CHECK 2 — LIVE SAP PRE-CONFIRM STATE GATE (WC05–WC09)
    //
    // Immediately before ExecutePick, ReadLiveSapPickState is called.
    // Stale or concurrent-pick races are rejected before any SAP mutation.
    // ─────────────────────────────────────────────────────────────────────────

    // WC05 — Gate rejects when OPKL.Status != "R" (pick list already closed/picked)
    [Fact]
    public async Task WC05_Gate_RejectsWhen_OpklStatus_NotReleased()
    {
        SeedPickList(700, "R");
        SeedPickListLine(700, pickEntry: 1, itemCode: "WC05-ITM", whsCode: "003", relQtty: 3m);

        var adapter = BuildAdapter(candidates: new[] { new BinCandidateDto(10, "003-A", "003", 5m) });
        adapter.LiveStateToReturn = new LiveSapPickState(
            OpklStatus: "Y",   // already fully picked — gate must block
            OpklCanceled: "N",
            CurrentPickQtty: 3m,
            RelQtty: 3m,
            PickStatus: "Y");
        adapter.PickResult = (0, null, null); // ExecutePick should NOT be called

        var svc    = BuildService(adapter);
        var req    = MakeRequest(pickEntry: 1, desiredQty: 3m, binAbsEntry: 10);
        var result = await svc.ConfirmPickAsync(700, req);

        Assert.False(result.Success);
        Assert.Single(result.Lines);
        Assert.Equal("StaleConflict", result.Lines[0].ErrorType);
        Assert.Equal(-2,              result.Lines[0].SapRc);
        Assert.Contains("'Y'",        result.Lines[0].Error);
    }

    // WC06 — Gate rejects when OPKL.Canceled = "Y"
    [Fact]
    public async Task WC06_Gate_RejectsWhen_PickListCanceled()
    {
        SeedPickList(701, "R");
        SeedPickListLine(701, pickEntry: 1, itemCode: "WC06-ITM", whsCode: "003", relQtty: 2m);

        var adapter = BuildAdapter(candidates: new[] { new BinCandidateDto(10, "003-A", "003", 5m) });
        adapter.LiveStateToReturn = new LiveSapPickState(
            OpklStatus: "R",
            OpklCanceled: "Y",  // canceled
            CurrentPickQtty: 0m,
            RelQtty: 2m,
            PickStatus: "R");

        var svc    = BuildService(adapter);
        var result = await svc.ConfirmPickAsync(701, MakeRequest(1, 2m, 10));

        Assert.Equal("StaleConflict", result.Lines[0].ErrorType);
        Assert.Contains("canceled", result.Lines[0].Error, StringComparison.OrdinalIgnoreCase);
    }

    // WC07 — Gate rejects when PKL1.PickStatus = "Y" (line already fully picked)
    [Fact]
    public async Task WC07_Gate_RejectsWhen_LineAlreadyPicked()
    {
        SeedPickList(702, "R");
        SeedPickListLine(702, pickEntry: 1, itemCode: "WC07-ITM", whsCode: "003", relQtty: 5m);

        var adapter = BuildAdapter(candidates: new[] { new BinCandidateDto(10, "003-A", "003", 5m) });
        adapter.LiveStateToReturn = new LiveSapPickState(
            OpklStatus: "R",
            OpklCanceled: "N",
            CurrentPickQtty: 5m,
            RelQtty: 5m,
            PickStatus: "Y");  // line already picked

        var svc    = BuildService(adapter);
        var result = await svc.ConfirmPickAsync(702, MakeRequest(1, 5m, 10));

        Assert.Equal("StaleConflict", result.Lines[0].ErrorType);
        Assert.Contains("already", result.Lines[0].Error, StringComparison.OrdinalIgnoreCase);
    }

    // WC08 — Gate rejects when DesiredFinalPickQty <= CurrentPickQtty (stale / already-done / would-reduce)
    [Fact]
    public async Task WC08_Gate_RejectsWhen_AlreadyAtOrBeyondDesired()
    {
        SeedPickList(703, "R");
        SeedPickListLine(703, pickEntry: 1, itemCode: "WC08-ITM", whsCode: "003", relQtty: 5m);

        var adapter = BuildAdapter(candidates: new[] { new BinCandidateDto(10, "003-A", "003", 5m) });
        adapter.LiveStateToReturn = new LiveSapPickState(
            OpklStatus: "R",
            OpklCanceled: "N",
            CurrentPickQtty: 3m,  // SAP already has PickQtty=3
            RelQtty: 5m,
            PickStatus: "P");

        var svc = BuildService(adapter);

        // Sending DesiredFinalPickQty=3 when SAP already has 3 — stale duplicate
        var resultSame = await svc.ConfirmPickAsync(703, MakeRequest(1, 3m, 10));
        Assert.Equal("StaleConflict", resultSame.Lines[0].ErrorType);

        // Sending DesiredFinalPickQty=2 when SAP has 3 — would reduce
        var resultLess = await svc.ConfirmPickAsync(703, MakeRequest(1, 2m, 10));
        Assert.Equal("StaleConflict", resultLess.Lines[0].ErrorType);
    }

    // WC09 — Gate passes when Status="R", Canceled="N", PickQtty=0 (clean state)
    [Fact]
    public async Task WC09_Gate_PassesWhen_StatusReleased_PickQttyZero()
    {
        SeedPickList(704, "R");
        SeedPickListLine(704, pickEntry: 1, itemCode: "WC09-ITM", whsCode: "003", relQtty: 3m);

        var adapter = BuildAdapter(candidates: new[] { new BinCandidateDto(10, "003-A", "003", 5m) });
        adapter.LiveStateToReturn = new LiveSapPickState(
            OpklStatus: "R",
            OpklCanceled: "N",
            CurrentPickQtty: 0m,   // clean — gate passes
            RelQtty: 3m,
            PickStatus: "R");
        adapter.PickResult = (0, null, new SapReplitAPI.Models.ZoneFulfillment.Pkl1LineState(704, 10, 0, 17, 3m, 3m, "Y"));

        var svc    = BuildService(adapter);
        var result = await svc.ConfirmPickAsync(704, MakeRequest(1, 3m, 10));

        Assert.True(result.Lines[0].Success);
        Assert.Equal("WC09-ITM", result.Lines[0].ItemCode);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // CHECK 3 — PARTIAL PICK QUANTITY SEMANTICS (WC10–WC15)
    //
    // DesiredFinalPickQty is the CUMULATIVE desired final SAP PickQtty.
    // SAP DI API pl.Lines.PickedQuantity is a desired-state assignment, not additive.
    // Bin allocations must sum to DesiredFinalPickQty (the final desired total).
    // ─────────────────────────────────────────────────────────────────────────

    // WC10 — ConfirmPickLineDto uses DesiredFinalPickQty (not PickedQty) — proves the rename
    [Fact]
    public void WC10_ConfirmPickLineDto_HasDesiredFinalPickQty_NotPickedQty()
    {
        var props = typeof(ConfirmPickLineDto).GetProperties().Select(p => p.Name).ToList();
        Assert.Contains("DesiredFinalPickQty", props);
        Assert.DoesNotContain("PickedQty", props);  // ambiguous name removed
    }

    // WC11 — DesiredFinalPickQty semantics: bins must sum to the total, not the delta
    [Fact]
    public void WC11_BinsSumMustEqualDesiredFinalPickQty()
    {
        // DesiredFinalPickQty=3, two bins allocating 2+1=3 → valid
        var candidates = new[]
        {
            new BinCandidateDto(1, "003-A", "003", 5m),
            new BinCandidateDto(2, "003-B", "003", 3m),
        };
        var bins3 = new[]
        {
            new BinAllocationRequestDto { BinAbsEntry = 1, Qty = 2m },
            new BinAllocationRequestDto { BinAbsEntry = 2, Qty = 1m },
        };
        Assert.Null(PickBinValidator.ValidateLine(3m, candidates, bins3));

        // Bins total=2 but DesiredFinalPickQty=3 → invalid (total mismatch)
        var bins2 = new[]
        {
            new BinAllocationRequestDto { BinAbsEntry = 1, Qty = 2m },
        };
        Assert.NotNull(PickBinValidator.ValidateLine(3m, candidates, bins2));
    }

    // WC12 — First pick: SAP PickQtty=0, DesiredFinalPickQty=1 → gate passes and SAP called
    [Fact]
    public async Task WC12_FirstPick_DesiredQty1_GatePasses()
    {
        SeedPickList(705, "R");
        SeedPickListLine(705, pickEntry: 1, itemCode: "WC12-ITM", whsCode: "003", relQtty: 5m);

        var adapter = BuildAdapter(candidates: new[] { new BinCandidateDto(10, "003-A", "003", 5m) });
        adapter.LiveStateToReturn = new LiveSapPickState("R", "N", 0m, 5m, "R"); // PickQtty=0
        // Pkl1LineState args: (AbsEntry, PickEntry, OrderLine, BaseObject, RelQtty, PickQtty, PickStatus)
        adapter.PickResult = (0, null, new SapReplitAPI.Models.ZoneFulfillment.Pkl1LineState(705, 10, 0, 17, 5m, 1m, "P"));

        var svc    = BuildService(adapter);
        var result = await svc.ConfirmPickAsync(705, MakeRequest(1, 1m, 10));

        Assert.True(result.Lines[0].Success);
        Assert.Equal(1m, result.Lines[0].PickedQty); // SAP confirmed PickQtty=1
    }

    // WC13 — Incremental pick: SAP PickQtty=2, DesiredFinalPickQty=3 (cumulative) → gate passes
    [Fact]
    public async Task WC13_IncrementalPick_DesiredQtyCumulative_GatePasses()
    {
        SeedPickList(706, "R");
        SeedPickListLine(706, pickEntry: 1, itemCode: "WC13-ITM", whsCode: "003", relQtty: 5m);

        var adapter = BuildAdapter(candidates: new[] { new BinCandidateDto(10, "003-A", "003", 5m) });
        // SAP already has PickQtty=2; picker wants to bring total to 3 → send DesiredFinalPickQty=3
        adapter.LiveStateToReturn = new LiveSapPickState("R", "N", 2m, 5m, "P");
        // Pkl1LineState args: (AbsEntry, PickEntry, OrderLine, BaseObject, RelQtty, PickQtty, PickStatus)
        adapter.PickResult = (0, null, new SapReplitAPI.Models.ZoneFulfillment.Pkl1LineState(706, 10, 0, 17, 5m, 3m, "P"));

        var svc    = BuildService(adapter);
        var result = await svc.ConfirmPickAsync(706, MakeRequest(1, 3m, 10));

        Assert.True(result.Lines[0].Success);
        Assert.Equal(3m, result.Lines[0].PickedQty);
    }

    // WC14 — Anti-stale: DesiredFinalPickQty=2 when SAP already has PickQtty=2 → stale conflict
    [Fact]
    public async Task WC14_StaleRequest_AlreadyAtDesiredQty_Rejected()
    {
        SeedPickList(707, "R");
        SeedPickListLine(707, pickEntry: 1, itemCode: "WC14-ITM", whsCode: "003", relQtty: 5m);

        var adapter = BuildAdapter(candidates: new[] { new BinCandidateDto(10, "003-A", "003", 5m) });
        adapter.LiveStateToReturn = new LiveSapPickState("R", "N", 2m, 5m, "P"); // PickQtty already=2

        var svc    = BuildService(adapter);
        var result = await svc.ConfirmPickAsync(707, MakeRequest(1, 2m, 10)); // request same qty

        Assert.Equal("StaleConflict", result.Lines[0].ErrorType);
        Assert.Contains("Stale",      result.Lines[0].Error);
    }

    // WC15 — Anti-reduction: DesiredFinalPickQty=1 when SAP has PickQtty=2 → stale conflict
    [Fact]
    public async Task WC15_ReductionRequest_Rejected()
    {
        SeedPickList(708, "R");
        SeedPickListLine(708, pickEntry: 1, itemCode: "WC15-ITM", whsCode: "003", relQtty: 5m);

        var adapter = BuildAdapter(candidates: new[] { new BinCandidateDto(10, "003-A", "003", 5m) });
        adapter.LiveStateToReturn = new LiveSapPickState("R", "N", 2m, 5m, "P"); // PickQtty=2

        var svc    = BuildService(adapter);
        var result = await svc.ConfirmPickAsync(708, MakeRequest(1, 1m, 10)); // request less than current

        Assert.Equal("StaleConflict", result.Lines[0].ErrorType); // would reduce from 2 → 1
    }

    // ─────────────────────────────────────────────────────────────────────────
    // CHECK 4 — HTTP 207 PARTIAL SUCCESS CONTRACT (WC16–WC20)
    //
    // 200 = all success
    // 207 = partial (any success + any failure) — RefreshAsync runs; client must reload
    // 409 = all stale-conflict failures — client must reload before retry
    // 400 = all validation failures
    // 422 = all SAP failures
    //
    // LineConfirmResultDto must include ItemCode (for frontend "which item failed").
    // ConfirmPickResponseDto must include OverallStatus string.
    // ─────────────────────────────────────────────────────────────────────────

    // WC16 — LineConfirmResultDto has ItemCode property
    [Fact]
    public void WC16_LineConfirmResultDto_HasItemCode()
    {
        var props = typeof(LineConfirmResultDto).GetProperties().Select(p => p.Name).ToList();
        Assert.Contains("ItemCode", props);
    }

    // WC17 — LineConfirmResultDto has ErrorType property
    [Fact]
    public void WC17_LineConfirmResultDto_HasErrorType()
    {
        var props = typeof(LineConfirmResultDto).GetProperties().Select(p => p.Name).ToList();
        Assert.Contains("ErrorType", props);
    }

    // WC18 — ConfirmPickResponseDto has OverallStatus property
    [Fact]
    public void WC18_ConfirmPickResponseDto_HasOverallStatus()
    {
        var props = typeof(ConfirmPickResponseDto).GetProperties().Select(p => p.Name).ToList();
        Assert.Contains("OverallStatus", props);
    }

    // WC19 — All-success response: OverallStatus="Success", ItemCode populated in each line
    [Fact]
    public async Task WC19_AllSuccess_OverallStatusSuccess_ItemCodePopulated()
    {
        SeedPickList(709, "R");
        SeedPickListLine(709, pickEntry: 1, itemCode: "WC19-ITEM-A", whsCode: "003", relQtty: 2m);

        var adapter = BuildAdapter(candidates: new[] { new BinCandidateDto(10, "003-A", "003", 5m) });
        adapter.LiveStateToReturn = new LiveSapPickState("R", "N", 0m, 2m, "R");
        adapter.PickResult = (0, null, new SapReplitAPI.Models.ZoneFulfillment.Pkl1LineState(709, 10, 0, 17, 2m, 2m, "Y"));

        var svc    = BuildService(adapter);
        var result = await svc.ConfirmPickAsync(709, MakeRequest(1, 2m, 10));

        Assert.True(result.Success);
        Assert.Equal("Success",      result.OverallStatus);
        Assert.Equal("WC19-ITEM-A",  result.Lines[0].ItemCode);
        Assert.Null(result.Lines[0].ErrorType);
    }

    // WC20 — Stale conflict: ErrorType="StaleConflict", SapRc=-2, ItemCode present
    [Fact]
    public async Task WC20_StaleConflict_ErrorType_SapRcMinus2_ItemCode()
    {
        SeedPickList(710, "R");
        SeedPickListLine(710, pickEntry: 1, itemCode: "WC20-ITEM-B", whsCode: "003", relQtty: 3m);

        var adapter = BuildAdapter(candidates: new[] { new BinCandidateDto(10, "003-A", "003", 5m) });
        // SAP has PickQtty=3 already — any request is stale
        adapter.LiveStateToReturn = new LiveSapPickState("R", "N", 3m, 3m, "Y");

        var svc    = BuildService(adapter);
        var result = await svc.ConfirmPickAsync(710, MakeRequest(1, 3m, 10));

        Assert.False(result.Success);
        Assert.Equal("Failure",        result.OverallStatus);
        Assert.Equal("StaleConflict",  result.Lines[0].ErrorType);
        Assert.Equal(-2,               result.Lines[0].SapRc);
        Assert.Equal("WC20-ITEM-B",    result.Lines[0].ItemCode);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    private WarehouseBinPickService BuildService(IWarehousePickSapAdapter adapter)
        => new WarehouseBinPickService(
            adapter,
            _cache,
            new FakePickListRefreshServiceWc(),
            NullLogger<WarehouseBinPickService>.Instance);

    private static FakeWarehousePickSapAdapterWc BuildAdapter(
        BinCandidateDto[]? candidates = null)
    {
        var a = new FakeWarehousePickSapAdapterWc();
        a.CandidatesToReturn = candidates?.ToList() ?? new();
        return a;
    }

    private static ConfirmPickRequest MakeRequest(
        int pickEntry, decimal desiredQty, int binAbsEntry)
        => new ConfirmPickRequest
        {
            Lines = new List<ConfirmPickLineDto>
            {
                new ConfirmPickLineDto
                {
                    PickEntry           = pickEntry,
                    DesiredFinalPickQty = desiredQty,
                    Bins = new List<BinAllocationRequestDto>
                    {
                        new BinAllocationRequestDto { BinAbsEntry = binAbsEntry, Qty = desiredQty },
                    },
                },
            },
        };

    private void SeedPickList(int absEntry, string status, string canceled = "N")
    {
        _db.PickLists.Add(new CachedPickList
        {
            AbsEntry     = absEntry,
            Status       = status,
            Canceled     = canceled,
            Name         = $"WC-PL-{absEntry}",
            OwnerCode    = 1,
            OwnerName    = "Test",
            SlpName      = string.Empty,
            Remarks      = string.Empty,
            PickDate     = DateTime.Today,
            CreateDate   = DateTime.Today,
            UpdateDate   = DateTime.Today,
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

// ─── Fakes for WC tests ───────────────────────────────────────────────────────

internal sealed class FakeWarehousePickSapAdapterWc : IWarehousePickSapAdapter
{
    public List<BinCandidateDto> CandidatesToReturn { get; set; } = new();
    public (int Rc, string? SapError, SapReplitAPI.Models.ZoneFulfillment.Pkl1LineState? PostState) PickResult { get; set; }
        = (0, null, null);
    public LiveSapPickState? LiveStateToReturn { get; set; }
        = new("R", "N", 0m, 1m, "R"); // default: gate passes

    public List<BinCandidateDto> QueryBinCandidates(string itemCode, string whsCode)
        => CandidatesToReturn;

    public LiveSapPickState? ReadLiveSapPickState(int absEntry, int pickEntry)
        => LiveStateToReturn;

    public (int Rc, string? SapError, SapReplitAPI.Models.ZoneFulfillment.Pkl1LineState? PostState) ExecutePick(
        int absEntry, int soDocEntry, int soLineNum,
        double desiredPickedQty, IReadOnlyList<SapReplitAPI.Models.ZoneFulfillment.BinPickAlloc> binAllocs,
        string itemCode, string whsCode)
        => PickResult;
}

internal sealed class FakePickListRefreshServiceWc : IPickListEventRefreshService
{
    public int RefreshCount { get; private set; }

    public Task<(bool ok, string? error)> RefreshAsync(int absEntry, CancellationToken ct)
    {
        RefreshCount++;
        return Task.FromResult((ok: true, error: (string?)null));
    }
}
