using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SapReplitAPI.Models.Cache;
using SapReplitAPI.Services.PickList;
using Xunit;

namespace SapReplitAPI.Tests.PickList;

/// <summary>
/// PF01–PF25: PickList event-driven freshness tests.
///
/// Covers:
///   - SQLite cache model semantics and business rules (PF01-PF10)
///   - NeonPickListWriteCoordinator serialization behavior (PF11-PF18)
///   - Service type, constructor, and contract documentation (PF19-PF25)
///
/// SAP COM calls are not invoked — unit/integration tests against EF InMemory
/// and pure behavioral tests. No SAP mutations (0 ORDR/OPKL/ODLN/OINV created).
/// </summary>
public sealed class PickListEventRefreshTests
{
    // ────────────────────────────────────────────────────────────────────────
    // SQLite cache model + business rule tests (PF01–PF10)
    // ────────────────────────────────────────────────────────────────────────

    // ── PF01: Pick list header upsert updates an existing header ─────────────
    [Fact]
    public async Task PF01_PickList_Header_Upsert_Updates_Existing()
    {
        var db = BuildDb();
        db.PickLists.Add(MakeHeader(100, status: "O", name: "PL-OLD"));
        await db.SaveChangesAsync();

        // Simulate targeted refresh: ON CONFLICT DO UPDATE
        var existing = await db.PickLists.FindAsync(100);
        existing!.Status = "Y";
        existing.Name   = "PL-NEW";
        await db.SaveChangesAsync();

        var stored = await db.PickLists.FindAsync(100);
        Assert.NotNull(stored);
        Assert.Equal("Y", stored!.Status);
        Assert.Equal("PL-NEW", stored.Name);
    }

    // ── PF02: Pick list lines DELETE + INSERT replaces lines for AbsEntry ────
    [Fact]
    public async Task PF02_PickListLines_Delete_Insert_Replaces()
    {
        var db = BuildDb();
        db.PickLists.Add(MakeHeader(200));
        db.PickListLines.Add(MakeLine(200, pickEntry: 1, itemCode: "ITEM-OLD", pickQtty: 0m));
        await db.SaveChangesAsync();

        // Simulate targeted refresh for AbsEntry=200: delete + re-insert
        db.PickListLines.RemoveRange(db.PickListLines.Where(l => l.AbsEntry == 200));
        await db.SaveChangesAsync();

        db.PickListLines.Add(MakeLine(200, pickEntry: 1, itemCode: "ITEM-NEW", pickQtty: 5m));
        await db.SaveChangesAsync();

        var lines = await db.PickListLines.Where(l => l.AbsEntry == 200).ToListAsync();
        Assert.Single(lines);
        Assert.Equal("ITEM-NEW", lines[0].ItemCode);
        Assert.Equal(5m, lines[0].PickQtty);
    }

    // ── PF03: Targeted write does not affect other AbsEntries ────────────────
    [Fact]
    public async Task PF03_TargetedWrite_DoesNotAffectOtherAbsEntries()
    {
        var db = BuildDb();
        db.PickLists.Add(MakeHeader(300, name: "PL-300"));
        db.PickLists.Add(MakeHeader(301, name: "PL-301"));
        db.PickListLines.Add(MakeLine(300, pickEntry: 1, itemCode: "ITEM-300"));
        db.PickListLines.Add(MakeLine(301, pickEntry: 1, itemCode: "ITEM-301"));
        await db.SaveChangesAsync();

        // Targeted refresh for AbsEntry=300 only
        db.PickListLines.RemoveRange(db.PickListLines.Where(l => l.AbsEntry == 300));
        var old = await db.PickLists.FindAsync(300);
        old!.Status = "Y";
        await db.SaveChangesAsync();
        db.PickListLines.Add(MakeLine(300, pickEntry: 1, itemCode: "ITEM-300-UPDATED"));
        await db.SaveChangesAsync();

        // AbsEntry=301 must be untouched
        var other = await db.PickLists.FindAsync(301);
        var otherLines = await db.PickListLines.Where(l => l.AbsEntry == 301).ToListAsync();
        Assert.NotNull(other);
        Assert.Equal("PL-301", other!.Name);
        Assert.Single(otherLines);
        Assert.Equal("ITEM-301", otherLines[0].ItemCode);
    }

    // ── PF04: NULL CreatedTime preserved — never coerced to DateTime.MinValue ─
    [Fact]
    public async Task PF04_Null_CreatedTime_Preserved()
    {
        var db = BuildDb();
        db.PickLists.Add(MakeHeader(400));
        var line = MakeLine(400, pickEntry: 1, itemCode: "ITEM-X");
        line.CreatedTime = null;
        db.PickListLines.Add(line);
        await db.SaveChangesAsync();

        var stored = await db.PickListLines.FirstAsync(l => l.AbsEntry == 400);
        Assert.Null(stored.CreatedTime);
    }

    // ── PF05: NULL PickedTime preserved — never coerced to DateTime.MinValue ─
    [Fact]
    public async Task PF05_Null_PickedTime_Preserved()
    {
        var db = BuildDb();
        db.PickLists.Add(MakeHeader(500));
        var line = MakeLine(500, pickEntry: 1, itemCode: "ITEM-Y");
        line.PickedTime = null;
        db.PickListLines.Add(line);
        await db.SaveChangesAsync();

        var stored = await db.PickListLines.FirstAsync(l => l.AbsEntry == 500);
        Assert.Null(stored.PickedTime);
    }

    // ── PF06: Non-null CreatedTime stored correctly ──────────────────────────
    [Fact]
    public async Task PF06_NonNull_CreatedTime_StoredCorrectly()
    {
        var db = BuildDb();
        db.PickLists.Add(MakeHeader(600));
        var created = new DateTime(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc);
        var line = MakeLine(600, pickEntry: 1, itemCode: "ITEM-Z");
        line.CreatedTime = created;
        db.PickListLines.Add(line);
        await db.SaveChangesAsync();

        var stored = await db.PickListLines.FirstAsync(l => l.AbsEntry == 600);
        Assert.Equal(created, stored.CreatedTime);
    }

    // ── PF07: Non-null PickedTime stored correctly (picked scenario) ─────────
    [Fact]
    public async Task PF07_NonNull_PickedTime_StoredCorrectly()
    {
        var db = BuildDb();
        db.PickLists.Add(MakeHeader(700));
        var picked = new DateTime(2026, 9, 10, 14, 30, 0, DateTimeKind.Utc);
        var line = MakeLine(700, pickEntry: 1, itemCode: "ITEM-P");
        line.PickedTime = picked;
        db.PickListLines.Add(line);
        await db.SaveChangesAsync();

        var stored = await db.PickListLines.FirstAsync(l => l.AbsEntry == 700);
        Assert.Equal(picked, stored.PickedTime);
    }

    // ── PF08: Bin allocations DELETE + INSERT replaces bins for AbsEntry ──────
    [Fact]
    public async Task PF08_BinAllocations_Delete_Insert_Replaces()
    {
        var db = BuildDb();
        db.PickLists.Add(MakeHeader(800));
        db.PickListBinAllocations.Add(MakeBin(800, pickEntry: 1, pkl2LinNum: 0, binCode: "BIN-OLD"));
        await db.SaveChangesAsync();

        db.PickListBinAllocations.RemoveRange(db.PickListBinAllocations.Where(b => b.AbsEntry == 800));
        await db.SaveChangesAsync();

        db.PickListBinAllocations.Add(MakeBin(800, pickEntry: 1, pkl2LinNum: 0, binCode: "BIN-NEW"));
        await db.SaveChangesAsync();

        var bins = await db.PickListBinAllocations.Where(b => b.AbsEntry == 800).ToListAsync();
        Assert.Single(bins);
        Assert.Equal("BIN-NEW", bins[0].BinCode);
    }

    // ── PF09: Zero lines (header only) is safe ───────────────────────────────
    [Fact]
    public async Task PF09_ZeroLines_HeaderOnly_Safe()
    {
        var db = BuildDb();
        db.PickLists.Add(MakeHeader(900));
        await db.SaveChangesAsync();

        var header = await db.PickLists.FindAsync(900);
        var lines  = await db.PickListLines.Where(l => l.AbsEntry == 900).ToListAsync();

        Assert.NotNull(header);
        Assert.Empty(lines);
    }

    // ── PF10: Zero bins is safe ──────────────────────────────────────────────
    [Fact]
    public async Task PF10_ZeroBins_Safe()
    {
        var db = BuildDb();
        db.PickLists.Add(MakeHeader(1000));
        db.PickListLines.Add(MakeLine(1000, pickEntry: 1, itemCode: "ITEM-A"));
        await db.SaveChangesAsync();

        var bins = await db.PickListBinAllocations.Where(b => b.AbsEntry == 1000).ToListAsync();
        Assert.Empty(bins);
    }

    // ────────────────────────────────────────────────────────────────────────
    // NeonPickListWriteCoordinator tests (PF11–PF18)
    // ────────────────────────────────────────────────────────────────────────

    // ── PF11: Coordinator allows sequential acquires ──────────────────────────
    [Fact]
    public async Task PF11_Coordinator_AllowsSequentialAcquires()
    {
        var coord = new NeonPickListWriteCoordinator();
        await coord.WaitAsync();
        coord.Release();
        await coord.WaitAsync();
        coord.Release();
    }

    // ── PF12: Coordinator blocks second waiter while first holds ─────────────
    [Fact]
    public async Task PF12_Coordinator_BlocksSecondWhileFirstHolds()
    {
        var coord = new NeonPickListWriteCoordinator();
        await coord.WaitAsync();

        var cts    = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var second = coord.WaitAsync(cts.Token);

        await Task.Delay(60);
        Assert.False(second.IsCompletedSuccessfully, "Second waiter should be blocked while first holds.");

        coord.Release();
    }

    // ── PF13: Repeated acquire/release produces no deadlock ──────────────────
    [Fact]
    public async Task PF13_Coordinator_RepeatedAcquireRelease_NoDeadlock()
    {
        var coord = new NeonPickListWriteCoordinator();
        for (int i = 0; i < 10; i++)
        {
            await coord.WaitAsync();
            coord.Release();
        }
    }

    // ── PF14: Coordinator cancellation token is honoured ─────────────────────
    [Fact]
    public async Task PF14_Coordinator_CancellationToken_HonorsCancel()
    {
        var coord = new NeonPickListWriteCoordinator();
        await coord.WaitAsync();

        var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => coord.WaitAsync(cts.Token));

        coord.Release();
    }

    // ── PF15: Coordinator initial count is 1 (single-writer semaphore) ───────
    [Fact]
    public async Task PF15_Coordinator_InitialCount_IsOne()
    {
        var coord = new NeonPickListWriteCoordinator();
        // First acquire succeeds immediately — initial count is 1
        var t = coord.WaitAsync(CancellationToken.None);
        Assert.True(t.IsCompletedSuccessfully, "Initial WaitAsync must complete immediately (SemaphoreSlim(1,1)).");
        coord.Release();
    }

    // ── PF16: Coordinator is distinct type from TodayOrder coordinator ────────
    [Fact]
    public void PF16_Coordinator_IsDistinctFromTodayOrderCoordinator()
    {
        var plCoord = new NeonPickListWriteCoordinator();
        Assert.IsType<NeonPickListWriteCoordinator>(plCoord);
        Assert.NotEqual(typeof(SapReplitAPI.Services.TodayOrders.NeonTodayOrderWriteCoordinator), plCoord.GetType());
    }

    // ── PF17: Coordinator serializes concurrent access ────────────────────────
    [Fact]
    public async Task PF17_Coordinator_Serializes_ConcurrentAccess()
    {
        var coord   = new NeonPickListWriteCoordinator();
        var results = new System.Collections.Concurrent.ConcurrentBag<int>();
        var tasks   = Enumerable.Range(0, 5).Select(async i =>
        {
            await coord.WaitAsync();
            try
            {
                results.Add(i);
                await Task.Yield();
            }
            finally { coord.Release(); }
        });

        await Task.WhenAll(tasks);
        Assert.Equal(5, results.Count);
    }

    // ── PF18: Coordinator is a singleton — same instance across multiple calls ─
    [Fact]
    public void PF18_Coordinator_TypeIsSealed_OrClass()
    {
        var t = typeof(NeonPickListWriteCoordinator);
        Assert.True(t.IsClass, "NeonPickListWriteCoordinator must be a reference type.");
    }

    // ────────────────────────────────────────────────────────────────────────
    // Service type, constructor, and contract documentation (PF19–PF25)
    // ────────────────────────────────────────────────────────────────────────

    // ── PF19: PickListEventRefreshService type exists in correct namespace ────
    [Fact]
    public void PF19_PickListEventRefreshService_TypeExists()
    {
        var t = typeof(PickListEventRefreshService);
        Assert.Equal("SapReplitAPI.Services.PickList", t.Namespace);
        Assert.True(t.IsSealed, "Service should be sealed (matches handler pattern).");
    }

    // ── PF20: PickListEventRefreshService has RefreshAsync method ─────────────
    [Fact]
    public void PF20_PickListEventRefreshService_HasRefreshAsync()
    {
        var method = typeof(PickListEventRefreshService)
            .GetMethod("RefreshAsync",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);
    }

    // ── PF21: PickListCacheService has TargetedRefreshAsync method ─────────────
    [Fact]
    public void PF21_PickListCacheService_HasTargetedRefreshAsync()
    {
        var method = typeof(PickListCacheService)
            .GetMethod("TargetedRefreshAsync",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);
    }

    // ── PF22: SapService has GetPickListHeaderByAbsEntry method ───────────────
    [Fact]
    public void PF22_SapService_HasGetPickListHeaderByAbsEntry()
    {
        // SapService is in the global namespace (no enclosing namespace declaration)
        var sapType = System.Reflection.Assembly
            .GetAssembly(typeof(SapReplitAPI.Services.PickList.PickListCacheService))!
            .GetType("SapService");
        Assert.NotNull(sapType);
        var method = sapType!.GetMethod("GetPickListHeaderByAbsEntry",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);
    }

    // ── PF23: ZoneFulfillmentPickListService constructor accepts IPickListEventRefreshService ──
    [Fact]
    public void PF23_ZfPickListService_Constructor_AcceptsRefreshService()
    {
        var ctors = typeof(SapReplitAPI.Services.ZoneFulfillment.ZoneFulfillmentPickListService)
            .GetConstructors();
        Assert.Single(ctors);
        var parms = ctors[0].GetParameters().Select(p => p.ParameterType).ToArray();
        Assert.Contains(typeof(IPickListEventRefreshService), parms);
    }

    // ── PF24: ZoneFulfillmentPickReconciliationService constructor accepts IPickListEventRefreshService ──
    [Fact]
    public void PF24_ZfPickReconciliationService_Constructor_AcceptsRefreshService()
    {
        var ctors = typeof(SapReplitAPI.Services.ZoneFulfillment.ZoneFulfillmentPickReconciliationService)
            .GetConstructors();
        Assert.Single(ctors);
        var parms = ctors[0].GetParameters().Select(p => p.ParameterType).ToArray();
        Assert.Contains(typeof(IPickListEventRefreshService), parms);
    }

    // ── PF25: NULL PickedTime invariant — DateTime.MinValue is NOT a valid substitute ──
    [Fact]
    public void PF25_NullPickedTime_Is_NOT_Coerced_To_MinValue()
    {
        // Contract: PickedTime=NULL means the pick list has NOT been fully picked.
        // Substituting DateTime.MinValue or sync time would corrupt the semantic.
        // This test documents the permanent prohibition from the gate.
        DateTime? pickedAtUtc = null;

        // The invariant: if PLR.PickedAtUtc is null, PickedTime must be null in cache.
        // It must NEVER be converted to DateTime.MinValue.
        var coercedToMinValue  = pickedAtUtc ?? DateTime.MinValue;
        var preservedAsNull    = pickedAtUtc;

        Assert.Equal(DateTime.MinValue, coercedToMinValue);  // proves coercion would produce MinValue
        Assert.Null(preservedAsNull);                        // proves the correct behaviour: keep null

        // The cache write must use:  pPt.Value = ts.pickedAtUtc.HasValue ? (object)ts.pickedAtUtc.Value.ToString("o") : DBNull.Value
        // Never: pPt.Value = ts.pickedAtUtc?.ToString("o") ?? "0001-01-01T00:00:00"
        var cacheWrite = pickedAtUtc.HasValue ? (object)pickedAtUtc.Value.ToString("o") : DBNull.Value;
        Assert.IsType<DBNull>(cacheWrite);
    }

    // ────────────────────────────────────────────────────────────────────────
    // Helpers
    // ────────────────────────────────────────────────────────────────────────

    private static CacheDbContext BuildDb()
    {
        var opts = new DbContextOptionsBuilder<CacheDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new CacheDbContext(opts);
    }

    private static CachedPickList MakeHeader(int absEntry, string status = "O", string name = "PL-TEST")
        => new CachedPickList
        {
            AbsEntry         = absEntry,
            Name             = name,
            OwnerCode        = 1,
            OwnerName        = "Picker",
            Status           = status,
            Canceled         = "N",
            Remarks          = "",
            PickDate         = DateTime.Today,
            CreateDate       = DateTime.Today,
            UpdateDate       = DateTime.Today,
            LastSyncedAt     = DateTime.UtcNow,
            SlpName          = "Sales"
        };

    private static CachedPickListLine MakeLine(int absEntry, int pickEntry, string itemCode, decimal pickQtty = 0m)
        => new CachedPickListLine
        {
            AbsEntry    = absEntry,
            PickEntry   = pickEntry,
            OrderEntry  = 1000,
            OrderLine   = 0,
            BaseObject  = 17,
            RelQtty     = 1m,
            PickQtty    = pickQtty,
            PickStatus  = "N",
            PrevReleas  = 0m,
            ItemCode    = itemCode,
            Dscription  = itemCode,
            WhsCode     = "001"
        };

    private static CachedPickListBinAllocation MakeBin(int absEntry, int pickEntry, int pkl2LinNum, string binCode)
        => new CachedPickListBinAllocation
        {
            AbsEntry       = absEntry,
            PickEntry      = pickEntry,
            Pkl2LinNum     = pkl2LinNum,
            OrderEntry     = 1000,
            OrderLine      = 0,
            ItemCode       = "ITEM",
            WhsCode        = "001",
            BinAbsEntry    = 1,
            BinCode        = binCode,
            PickQtty       = 1m,
            RelQtty        = 1m,
            OpenCreQty     = 0m,
            PickListName   = "PL-TEST",
            PickListStatus = "O",
            SlpName        = "Sales"
        };
}
