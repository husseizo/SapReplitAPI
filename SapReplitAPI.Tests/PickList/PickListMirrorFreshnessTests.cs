using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SapReplitAPI.Controllers;
using SapReplitAPI.Filters;
using SapReplitAPI.Jobs;
using SapReplitAPI.Models.Cache;
using SapReplitAPI.Services.PickList;
using Xunit;

namespace SapReplitAPI.Tests.PickList;

/// <summary>
/// PM01–PM30: PickList per-AbsEntry mirror freshness tests.
///
/// Covers:
///   - Non-terminal SQLite filter correctness (PM01-PM09)
///   - SAP change detection logic (PM10-PM18)
///   - Per-AbsEntry isolation: one failure does not block siblings (PM19-PM22)
///   - Separation of concerns: mirror freshness vs delivery automation (PM23-PM26)
///   - Hook controller type and contract (PM27-PM30)
///
/// No SAP COM calls — uses EF InMemory + FakeSapHeaderReader + FakeRefreshService.
/// No SAP mutations (0 ORDR/OPKL/ODLN/OINV created).
/// </summary>
public sealed class PickListMirrorFreshnessTests
{
    // ────────────────────────────────────────────────────────────────────────
    // Non-terminal filter correctness (PM01–PM09)
    // ────────────────────────────────────────────────────────────────────────

    // PM01: Status="O" (Open) is non-terminal — must be included
    [Fact]
    public async Task PM01_Status_Open_IsNonTerminal_Included()
    {
        var db     = BuildDb();
        var sap    = new FakeSapHeaderReader();
        var @ref   = new FakeRefreshService();
        var svc    = Build(db, sap, @ref);

        db.PickLists.Add(MakeHeader(1, status: "O", canceled: "N"));
        await db.SaveChangesAsync();

        sap.Register(1, MakeSapHeader(1, status: "Y", canceled: "N")); // status changed → trigger refresh

        await svc.RefreshNonTerminalAsync(default);

        Assert.Equal(1, @ref.CallCount);
        Assert.Contains(1, @ref.RefreshedEntries);
    }

    // PM02: Status="Y" (Picked) is terminal — must be excluded (no SAP call, no refresh)
    [Fact]
    public async Task PM02_Status_Picked_IsTerminal_Excluded()
    {
        var db     = BuildDb();
        var sap    = new FakeSapHeaderReader();
        var @ref   = new FakeRefreshService();
        var svc    = Build(db, sap, @ref);

        db.PickLists.Add(MakeHeader(2, status: "Y", canceled: "N"));
        await db.SaveChangesAsync();

        await svc.RefreshNonTerminalAsync(default);

        Assert.Equal(0, @ref.CallCount);
        Assert.Equal(0, sap.CallCount);
    }

    // PM03: Status="C" (Closed) is terminal — must be excluded
    [Fact]
    public async Task PM03_Status_Closed_IsTerminal_Excluded()
    {
        var db     = BuildDb();
        var sap    = new FakeSapHeaderReader();
        var @ref   = new FakeRefreshService();
        var svc    = Build(db, sap, @ref);

        db.PickLists.Add(MakeHeader(3, status: "C", canceled: "N"));
        await db.SaveChangesAsync();

        await svc.RefreshNonTerminalAsync(default);

        Assert.Equal(0, @ref.CallCount);
        Assert.Equal(0, sap.CallCount);
    }

    // PM04: Canceled="Y" is terminal — excluded even if Status="O"
    [Fact]
    public async Task PM04_Canceled_Y_IsTerminal_Excluded()
    {
        var db     = BuildDb();
        var sap    = new FakeSapHeaderReader();
        var @ref   = new FakeRefreshService();
        var svc    = Build(db, sap, @ref);

        db.PickLists.Add(MakeHeader(4, status: "O", canceled: "Y"));
        await db.SaveChangesAsync();

        await svc.RefreshNonTerminalAsync(default);

        Assert.Equal(0, @ref.CallCount);
        Assert.Equal(0, sap.CallCount);
    }

    // PM05: Status="P" (Partially Picked) is non-terminal — must be included
    [Fact]
    public async Task PM05_Status_PartiallyPicked_IsNonTerminal_Included()
    {
        var db     = BuildDb();
        var sap    = new FakeSapHeaderReader();
        var @ref   = new FakeRefreshService();
        var svc    = Build(db, sap, @ref);

        db.PickLists.Add(MakeHeader(5, status: "P", canceled: "N"));
        await db.SaveChangesAsync();

        sap.Register(5, MakeSapHeader(5, status: "Y", canceled: "N")); // fully picked → trigger

        await svc.RefreshNonTerminalAsync(default);

        Assert.Equal(1, @ref.CallCount);
        Assert.Contains(5, @ref.RefreshedEntries);
    }

    // PM06: Mixed statuses — only non-terminal PLs are queried from SAP
    [Fact]
    public async Task PM06_Mixed_Statuses_OnlyNonTerminalQueried()
    {
        var db     = BuildDb();
        var sap    = new FakeSapHeaderReader();
        var @ref   = new FakeRefreshService();
        var svc    = Build(db, sap, @ref);

        var today = DateTime.Today;
        db.PickLists.Add(MakeHeader(10, status: "O",  canceled: "N")); // non-terminal
        db.PickLists.Add(MakeHeader(11, status: "Y",  canceled: "N")); // terminal
        db.PickLists.Add(MakeHeader(12, status: "C",  canceled: "N")); // terminal
        db.PickLists.Add(MakeHeader(13, status: "O",  canceled: "Y")); // terminal (canceled)
        db.PickLists.Add(MakeHeader(14, status: "P",  canceled: "N")); // non-terminal
        await db.SaveChangesAsync();

        // No changes for AbsEntry 10 and 14 → no refreshes
        sap.Register(10, MakeSapHeader(10, status: "O", canceled: "N", updateDate: today));
        sap.Register(14, MakeSapHeader(14, status: "P", canceled: "N", updateDate: today));

        await svc.RefreshNonTerminalAsync(default);

        // SAP called only for non-terminal (10 and 14)
        Assert.Equal(2, sap.CallCount);
        Assert.Equal(0, @ref.CallCount);
    }

    // PM07: Empty SQLite — no candidates, no SAP calls
    [Fact]
    public async Task PM07_Empty_SQLite_NoCandidates()
    {
        var db  = BuildDb();
        var sap = new FakeSapHeaderReader();
        var @ref = new FakeRefreshService();
        var svc = Build(db, sap, @ref);

        await svc.RefreshNonTerminalAsync(default);

        Assert.Equal(0, sap.CallCount);
        Assert.Equal(0, @ref.CallCount);
    }

    // PM08: All candidates have no SAP change — zero refreshes
    [Fact]
    public async Task PM08_AllUnchanged_ZeroRefreshes()
    {
        var db     = BuildDb();
        var sap    = new FakeSapHeaderReader();
        var @ref   = new FakeRefreshService();
        var svc    = Build(db, sap, @ref);

        var today = DateTime.Today;
        db.PickLists.Add(MakeHeader(20, status: "O", canceled: "N", updateDate: today));
        db.PickLists.Add(MakeHeader(21, status: "O", canceled: "N", updateDate: today));
        await db.SaveChangesAsync();

        sap.Register(20, MakeSapHeader(20, status: "O", canceled: "N", updateDate: today));
        sap.Register(21, MakeSapHeader(21, status: "O", canceled: "N", updateDate: today));

        await svc.RefreshNonTerminalAsync(default);

        Assert.Equal(2, sap.CallCount);
        Assert.Equal(0, @ref.CallCount);
    }

    // PM09: All non-terminal, all changed — all refreshed
    [Fact]
    public async Task PM09_AllChanged_AllRefreshed()
    {
        var db     = BuildDb();
        var sap    = new FakeSapHeaderReader();
        var @ref   = new FakeRefreshService();
        var svc    = Build(db, sap, @ref);

        var yesterday = DateTime.Today.AddDays(-1);
        var today     = DateTime.Today;
        db.PickLists.Add(MakeHeader(30, status: "O", canceled: "N", updateDate: yesterday));
        db.PickLists.Add(MakeHeader(31, status: "O", canceled: "N", updateDate: yesterday));
        await db.SaveChangesAsync();

        sap.Register(30, MakeSapHeader(30, status: "Y", canceled: "N", updateDate: today));
        sap.Register(31, MakeSapHeader(31, status: "Y", canceled: "N", updateDate: today));

        await svc.RefreshNonTerminalAsync(default);

        Assert.Equal(2, @ref.CallCount);
        Assert.Contains(30, @ref.RefreshedEntries);
        Assert.Contains(31, @ref.RefreshedEntries);
    }

    // ────────────────────────────────────────────────────────────────────────
    // SAP change detection logic (PM10–PM18)
    // ────────────────────────────────────────────────────────────────────────

    // PM10: SAP.UpdateDate > cached.UpdateDate → detected as changed
    [Fact]
    public async Task PM10_UpdateDate_Advanced_Triggers_Refresh()
    {
        var db     = BuildDb();
        var sap    = new FakeSapHeaderReader();
        var @ref   = new FakeRefreshService();
        var svc    = Build(db, sap, @ref);

        var yesterday = DateTime.Today.AddDays(-1);
        var today     = DateTime.Today;
        db.PickLists.Add(MakeHeader(40, status: "O", canceled: "N", updateDate: yesterday));
        await db.SaveChangesAsync();

        sap.Register(40, MakeSapHeader(40, status: "O", canceled: "N", updateDate: today));

        await svc.RefreshNonTerminalAsync(default);

        Assert.Equal(1, @ref.CallCount);
        Assert.Contains(40, @ref.RefreshedEntries);
    }

    // PM11: Same UpdateDate, same Status, same Canceled → NOT changed
    [Fact]
    public async Task PM11_NoChange_NoRefresh()
    {
        var db     = BuildDb();
        var sap    = new FakeSapHeaderReader();
        var @ref   = new FakeRefreshService();
        var svc    = Build(db, sap, @ref);

        var today = DateTime.Today;
        db.PickLists.Add(MakeHeader(41, status: "O", canceled: "N", updateDate: today));
        await db.SaveChangesAsync();

        sap.Register(41, MakeSapHeader(41, status: "O", canceled: "N", updateDate: today));

        await svc.RefreshNonTerminalAsync(default);

        Assert.Equal(0, @ref.CallCount);
    }

    // PM12: SAP.Status changed (same date) → detected as changed
    [Fact]
    public async Task PM12_StatusChange_SameDate_Triggers_Refresh()
    {
        var db     = BuildDb();
        var sap    = new FakeSapHeaderReader();
        var @ref   = new FakeRefreshService();
        var svc    = Build(db, sap, @ref);

        var today = DateTime.Today;
        db.PickLists.Add(MakeHeader(42, status: "O", canceled: "N", updateDate: today));
        await db.SaveChangesAsync();

        // Same UpdateDate but Status flipped to partially picked
        sap.Register(42, MakeSapHeader(42, status: "P", canceled: "N", updateDate: today));

        await svc.RefreshNonTerminalAsync(default);

        Assert.Equal(1, @ref.CallCount);
        Assert.Contains(42, @ref.RefreshedEntries);
    }

    // PM13: SAP.Canceled changed to "Y" (same date) → detected as changed
    [Fact]
    public async Task PM13_CanceledChange_Triggers_Refresh()
    {
        var db     = BuildDb();
        var sap    = new FakeSapHeaderReader();
        var @ref   = new FakeRefreshService();
        var svc    = Build(db, sap, @ref);

        var today = DateTime.Today;
        db.PickLists.Add(MakeHeader(43, status: "O", canceled: "N", updateDate: today));
        await db.SaveChangesAsync();

        sap.Register(43, MakeSapHeader(43, status: "O", canceled: "Y", updateDate: today));

        await svc.RefreshNonTerminalAsync(default);

        Assert.Equal(1, @ref.CallCount);
        Assert.Contains(43, @ref.RefreshedEntries);
    }

    // PM14: SAP returns null for AbsEntry → skipped, no refresh
    [Fact]
    public async Task PM14_SapReturnsNull_Skipped()
    {
        var db     = BuildDb();
        var sap    = new FakeSapHeaderReader();
        var @ref   = new FakeRefreshService();
        var svc    = Build(db, sap, @ref);

        db.PickLists.Add(MakeHeader(44, status: "O", canceled: "N"));
        await db.SaveChangesAsync();

        // No registration in sap → returns null

        await svc.RefreshNonTerminalAsync(default);

        Assert.Equal(1, sap.CallCount);      // SAP was queried
        Assert.Equal(0, @ref.CallCount);     // but no refresh
    }

    // PM15: Only one of two PLs changed → only that one refreshed
    [Fact]
    public async Task PM15_PartialChange_OnlyChangedRefreshed()
    {
        var db     = BuildDb();
        var sap    = new FakeSapHeaderReader();
        var @ref   = new FakeRefreshService();
        var svc    = Build(db, sap, @ref);

        var today     = DateTime.Today;
        var yesterday = today.AddDays(-1);
        db.PickLists.Add(MakeHeader(50, status: "O", canceled: "N", updateDate: yesterday));
        db.PickLists.Add(MakeHeader(51, status: "O", canceled: "N", updateDate: today));
        await db.SaveChangesAsync();

        sap.Register(50, MakeSapHeader(50, status: "Y", canceled: "N", updateDate: today));   // changed
        sap.Register(51, MakeSapHeader(51, status: "O", canceled: "N", updateDate: today));   // unchanged

        await svc.RefreshNonTerminalAsync(default);

        Assert.Equal(1, @ref.CallCount);
        Assert.Contains(50, @ref.RefreshedEntries);
        Assert.DoesNotContain(51, @ref.RefreshedEntries);
    }

    // PM16: Multiple PLs changed → all refreshed independently
    [Fact]
    public async Task PM16_MultipleChanged_AllRefreshedIndependently()
    {
        var db     = BuildDb();
        var sap    = new FakeSapHeaderReader();
        var @ref   = new FakeRefreshService();
        var svc    = Build(db, sap, @ref);

        var yesterday = DateTime.Today.AddDays(-1);
        var today     = DateTime.Today;
        db.PickLists.Add(MakeHeader(60, status: "O", canceled: "N", updateDate: yesterday));
        db.PickLists.Add(MakeHeader(61, status: "O", canceled: "N", updateDate: yesterday));
        db.PickLists.Add(MakeHeader(62, status: "O", canceled: "N", updateDate: yesterday));
        await db.SaveChangesAsync();

        sap.Register(60, MakeSapHeader(60, status: "Y", canceled: "N", updateDate: today));
        sap.Register(61, MakeSapHeader(61, status: "P", canceled: "N", updateDate: today));
        sap.Register(62, MakeSapHeader(62, status: "O", canceled: "Y", updateDate: today));

        await svc.RefreshNonTerminalAsync(default);

        Assert.Equal(3, @ref.CallCount);
        Assert.Contains(60, @ref.RefreshedEntries);
        Assert.Contains(61, @ref.RefreshedEntries);
        Assert.Contains(62, @ref.RefreshedEntries);
    }

    // PM17: SAP.UpdateDate equal but slightly older → NOT changed (no regression)
    [Fact]
    public async Task PM17_SapDateOlder_NotChanged()
    {
        var db     = BuildDb();
        var sap    = new FakeSapHeaderReader();
        var @ref   = new FakeRefreshService();
        var svc    = Build(db, sap, @ref);

        var today     = DateTime.Today;
        var yesterday = today.AddDays(-1);
        db.PickLists.Add(MakeHeader(70, status: "O", canceled: "N", updateDate: today));
        await db.SaveChangesAsync();

        // SAP date is older than cached (cache already fresher than SAP — shouldn't happen in practice,
        // but must not trigger a refresh that could overwrite fresh data with stale data)
        sap.Register(70, MakeSapHeader(70, status: "O", canceled: "N", updateDate: yesterday));

        await svc.RefreshNonTerminalAsync(default);

        Assert.Equal(0, @ref.CallCount);
    }

    // PM18: RefreshAsync failure on one AbsEntry counted as error, does not re-throw
    [Fact]
    public async Task PM18_RefreshFailure_Counted_DoesNotRethrow()
    {
        var db     = BuildDb();
        var sap    = new FakeSapHeaderReader();
        var @ref   = new FakeRefreshService();
        var svc    = Build(db, sap, @ref);

        var yesterday = DateTime.Today.AddDays(-1);
        var today     = DateTime.Today;
        db.PickLists.Add(MakeHeader(80, status: "O", canceled: "N", updateDate: yesterday));
        await db.SaveChangesAsync();

        sap.Register(80, MakeSapHeader(80, status: "Y", canceled: "N", updateDate: today));
        @ref.FailOnAbsEntry(80); // simulate refresh failure

        // Must not throw
        await svc.RefreshNonTerminalAsync(default);

        Assert.Equal(1, @ref.CallCount);
    }

    // ────────────────────────────────────────────────────────────────────────
    // Per-AbsEntry isolation (PM19–PM22)
    // ────────────────────────────────────────────────────────────────────────

    // PM19: SAP throws for one AbsEntry → others still processed
    [Fact]
    public async Task PM19_SapException_ForOne_OthersContinue()
    {
        var db     = BuildDb();
        var sap    = new FakeSapHeaderReader();
        var @ref   = new FakeRefreshService();
        var svc    = Build(db, sap, @ref);

        var yesterday = DateTime.Today.AddDays(-1);
        var today     = DateTime.Today;
        db.PickLists.Add(MakeHeader(90, status: "O", canceled: "N", updateDate: yesterday));
        db.PickLists.Add(MakeHeader(91, status: "O", canceled: "N", updateDate: yesterday));
        await db.SaveChangesAsync();

        sap.ThrowOnAbsEntry(90);
        sap.Register(91, MakeSapHeader(91, status: "Y", canceled: "N", updateDate: today));

        await svc.RefreshNonTerminalAsync(default);

        // 91 still refreshed despite 90 throwing
        Assert.Equal(1, @ref.CallCount);
        Assert.Contains(91, @ref.RefreshedEntries);
    }

    // PM20: RefreshAsync throws for one AbsEntry → others still processed
    [Fact]
    public async Task PM20_RefreshException_ForOne_OthersContinue()
    {
        var db     = BuildDb();
        var sap    = new FakeSapHeaderReader();
        var @ref   = new FakeRefreshService();
        var svc    = Build(db, sap, @ref);

        var yesterday = DateTime.Today.AddDays(-1);
        var today     = DateTime.Today;
        db.PickLists.Add(MakeHeader(100, status: "O", canceled: "N", updateDate: yesterday));
        db.PickLists.Add(MakeHeader(101, status: "O", canceled: "N", updateDate: yesterday));
        await db.SaveChangesAsync();

        sap.Register(100, MakeSapHeader(100, status: "Y", canceled: "N", updateDate: today));
        sap.Register(101, MakeSapHeader(101, status: "Y", canceled: "N", updateDate: today));
        @ref.ThrowOnAbsEntry(100); // throws exception (not (false, error))

        await svc.RefreshNonTerminalAsync(default);

        // 101 still refreshed despite 100 throwing
        Assert.Contains(101, @ref.RefreshedEntries);
    }

    // PM21: SAP null for one AbsEntry → others still processed
    [Fact]
    public async Task PM21_SapNullForOne_OthersContinue()
    {
        var db     = BuildDb();
        var sap    = new FakeSapHeaderReader();
        var @ref   = new FakeRefreshService();
        var svc    = Build(db, sap, @ref);

        var yesterday = DateTime.Today.AddDays(-1);
        var today     = DateTime.Today;
        db.PickLists.Add(MakeHeader(110, status: "O", canceled: "N", updateDate: yesterday));
        db.PickLists.Add(MakeHeader(111, status: "O", canceled: "N", updateDate: yesterday));
        await db.SaveChangesAsync();

        // 110 not registered → returns null
        sap.Register(111, MakeSapHeader(111, status: "Y", canceled: "N", updateDate: today));

        await svc.RefreshNonTerminalAsync(default);

        // 111 still refreshed despite 110 null
        Assert.Equal(1, @ref.CallCount);
        Assert.Contains(111, @ref.RefreshedEntries);
        Assert.DoesNotContain(110, @ref.RefreshedEntries);
    }

    // PM22: CancellationToken cancellation exits the loop gracefully
    [Fact]
    public async Task PM22_Cancellation_ExitsGracefully()
    {
        var db     = BuildDb();
        var sap    = new FakeSapHeaderReader();
        var @ref   = new FakeRefreshService();
        var svc    = Build(db, sap, @ref);

        // Add multiple non-terminal PLs with changes
        var yesterday = DateTime.Today.AddDays(-1);
        var today     = DateTime.Today;
        for (int i = 200; i < 210; i++)
        {
            db.PickLists.Add(MakeHeader(i, status: "O", canceled: "N", updateDate: yesterday));
            sap.Register(i, MakeSapHeader(i, status: "Y", canceled: "N", updateDate: today));
        }
        await db.SaveChangesAsync();

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // cancel immediately

        // Must not throw OperationCanceledException — loop exits on ct.IsCancellationRequested check
        await svc.RefreshNonTerminalAsync(cts.Token);
        // Zero or very few refreshes — main thing is no exception thrown
    }

    // ────────────────────────────────────────────────────────────────────────
    // Separation of concerns: mirror freshness vs delivery automation (PM23–PM26)
    // ────────────────────────────────────────────────────────────────────────

    // PM23: PickListMirrorFreshnessService type is sealed
    [Fact]
    public void PM23_MirrorFreshnessService_IsSealed()
    {
        Assert.True(typeof(PickListMirrorFreshnessService).IsSealed);
    }

    // PM24: PickListCacheFreshnessJob has [DisallowConcurrentExecution]
    [Fact]
    public void PM24_CacheFreshnessJob_HasDisallowConcurrentExecution()
    {
        var attr = typeof(PickListCacheFreshnessJob)
            .GetCustomAttributes(typeof(Quartz.DisallowConcurrentExecutionAttribute), inherit: false);
        Assert.NotEmpty(attr);
    }

    // PM25: PickListCacheFreshnessJob constructor accepts PickListMirrorFreshnessService
    [Fact]
    public void PM25_CacheFreshnessJob_Constructor_AcceptsFreshnessService()
    {
        var ctors  = typeof(PickListCacheFreshnessJob).GetConstructors();
        Assert.Single(ctors);
        var parms  = ctors[0].GetParameters().Select(p => p.ParameterType).ToArray();
        Assert.Contains(typeof(PickListMirrorFreshnessService), parms);
    }

    // PM26: PickListMirrorFreshnessService does NOT depend on ZoneFulfillmentDeliveryCoordinator
    [Fact]
    public void PM26_MirrorFreshnessService_DoesNotDependOn_DeliveryCoordinator()
    {
        var ctors = typeof(PickListMirrorFreshnessService).GetConstructors();
        Assert.Single(ctors);
        var parms = ctors[0].GetParameters().Select(p => p.ParameterType).ToArray();

        var coordinatorType = typeof(SapReplitAPI.Services.ZoneFulfillment.ZoneFulfillmentDeliveryCoordinator);
        Assert.DoesNotContain(coordinatorType, parms);
    }

    // ────────────────────────────────────────────────────────────────────────
    // Hook controller type and contract (PM27–PM30)
    // ────────────────────────────────────────────────────────────────────────

    // PM27: PickListRefreshController is in the Controllers namespace
    [Fact]
    public void PM27_Controller_InCorrectNamespace()
    {
        Assert.Equal("SapReplitAPI.Controllers", typeof(PickListRefreshController).Namespace);
    }

    // PM28: PickListRefreshController is decorated with [ServiceFilter(typeof(ApiKeyAuthFilter))]
    [Fact]
    public void PM28_Controller_HasApiKeyAuthFilter()
    {
        var attrs = typeof(PickListRefreshController)
            .GetCustomAttributes(typeof(ServiceFilterAttribute), inherit: true)
            .Cast<ServiceFilterAttribute>()
            .ToList();

        Assert.Contains(attrs, a => a.ServiceType == typeof(ApiKeyAuthFilter));
    }

    // PM29: Controller has POST method for /internal/refresh/pick-list/{absEntry}
    [Fact]
    public void PM29_Controller_HasPostRefreshMethod()
    {
        var method = typeof(PickListRefreshController)
            .GetMethod("RefreshPickList",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);

        var httpPost = method!.GetCustomAttributes(typeof(HttpPostAttribute), inherit: false)
            .Cast<HttpPostAttribute>().FirstOrDefault();
        Assert.NotNull(httpPost);
        Assert.Equal("refresh/pick-list/{absEntry:int}", httpPost!.Template);
    }

    // PM30: Controller constructor accepts IPickListEventRefreshService
    [Fact]
    public void PM30_Controller_Constructor_AcceptsRefreshService()
    {
        var ctors  = typeof(PickListRefreshController).GetConstructors();
        Assert.Single(ctors);
        var parms  = ctors[0].GetParameters().Select(p => p.ParameterType).ToArray();
        Assert.Contains(typeof(IPickListEventRefreshService), parms);
    }

    // ────────────────────────────────────────────────────────────────────────
    // Fakes
    // ────────────────────────────────────────────────────────────────────────

    private sealed class FakeSapHeaderReader : IPickListSapHeaderReader
    {
        private readonly Dictionary<int, CachedPickList?> _responses = new();
        private readonly HashSet<int>                     _throws    = new();
        public int CallCount { get; private set; }

        public void Register(int absEntry, CachedPickList? header)
            => _responses[absEntry] = header;

        public void ThrowOnAbsEntry(int absEntry)
            => _throws.Add(absEntry);

        public CachedPickList? GetPickListHeaderByAbsEntry(int absEntry)
        {
            CallCount++;
            if (_throws.Contains(absEntry))
                throw new InvalidOperationException($"Simulated SAP error for AbsEntry={absEntry}");
            return _responses.TryGetValue(absEntry, out var h) ? h : null;
        }
    }

    private sealed class FakeRefreshService : IPickListEventRefreshService
    {
        private readonly HashSet<int> _fail   = new();
        private readonly HashSet<int> _throws = new();
        public int      CallCount        { get; private set; }
        public List<int> RefreshedEntries { get; } = new();

        public void FailOnAbsEntry(int absEntry)   => _fail.Add(absEntry);
        public void ThrowOnAbsEntry(int absEntry)  => _throws.Add(absEntry);

        public Task<(bool ok, string? error)> RefreshAsync(int absEntry, CancellationToken ct)
        {
            CallCount++;
            if (_throws.Contains(absEntry))
                throw new InvalidOperationException($"Simulated refresh throw for AbsEntry={absEntry}");
            if (_fail.Contains(absEntry))
                return Task.FromResult<(bool, string?)>((false, "simulated failure"));
            RefreshedEntries.Add(absEntry);
            return Task.FromResult<(bool, string?)>((true, null));
        }
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

    private static PickListMirrorFreshnessService Build(
        CacheDbContext db,
        FakeSapHeaderReader sap,
        FakeRefreshService @ref)
        => new PickListMirrorFreshnessService(
                sap, db, @ref,
                NullLogger<PickListMirrorFreshnessService>.Instance);

    private static CachedPickList MakeHeader(
        int      absEntry,
        string   status     = "O",
        string   canceled   = "N",
        DateTime updateDate = default)
        => new CachedPickList
        {
            AbsEntry     = absEntry,
            Name         = $"PL-{absEntry}",
            OwnerCode    = 1,
            OwnerName    = "Tester",
            Status       = status,
            Canceled     = canceled,
            Remarks      = "",
            PickDate     = DateTime.Today,
            CreateDate   = DateTime.Today,
            UpdateDate   = updateDate == default ? DateTime.Today : updateDate,
            LastSyncedAt = DateTime.UtcNow,
            SlpName      = "",
        };

    private static CachedPickList MakeSapHeader(
        int      absEntry,
        string   status     = "O",
        string   canceled   = "N",
        DateTime updateDate = default)
        => MakeHeader(absEntry, status, canceled, updateDate == default ? DateTime.Today : updateDate);
}
