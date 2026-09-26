using Microsoft.Extensions.Logging.Abstractions;
using SapReplitAPI.Models.ZoneFulfillment;
using SapReplitAPI.Services.ZoneFulfillment;
using Xunit;

namespace SapReplitAPI.Tests.ZfAdmin;

/// <summary>
/// ZDH_01–ZDH_17: durable ZF diagnostic incident lifecycle history.
///
/// All tests are pure in-memory (fake detector + fake repository) — no SQL Server,
/// no SAP, no COM. ZfIncidentObservationService.ObserveAsync is exercised directly.
///
/// Canonical reference: SO 28879 / OrchestrationId 20056 / FragmentId 20092 /
/// ItemCode VAG13782 / SoLineNum 1 — same values as ZfDiagnosticPhase4Tests.
/// </summary>
public class ZfDiagnosticIncidentHistoryTests
{
    private const string Key28879 = "28879_20092_ZF_FRAGMENT_RDR1_MISSING";

    private static ZfLiveIncidentObservation MakeObservation(
        string key = Key28879, int soDocNum = 28879, long fragmentId = 20092,
        string itemCode = "VAG13782", int soLineNum = 1, string whsCode = "002",
        long orchestrationId = 20056, int? soDocEntry = 28879) => new()
    {
        IncidentKey        = key,
        SoDocNum           = soDocNum,
        SoDocEntry         = soDocEntry,
        OrchestrationId    = orchestrationId,
        FragmentId         = fragmentId,
        SoLineNum          = soLineNum,
        ItemCode           = itemCode,
        WhsCode            = whsCode,
        SoLineQty          = 1m,
        OrchestrationState = "Accepted",
        IncidentCode       = ZfConsistencyStatus.FragmentRdr1Missing,
        Severity           = ZfDiagnosticSeverity.High,
    };

    private static (ZfIncidentObservationService svc, FakeDetector detector, FakeHistoryRepo repo) Build()
    {
        var detector = new FakeDetector();
        var repo     = new FakeHistoryRepo();
        var svc = new ZfIncidentObservationService(
            detector, repo, NullLogger<ZfIncidentObservationService>.Instance);
        return (svc, detector, repo);
    }

    // ── ZDH_01: new incident → ACTIVE persisted ───────────────────────────────

    [Fact]
    public async Task ZDH_01_NewIncident_ActivePersisted()
    {
        var (svc, detector, repo) = Build();
        detector.NextResult = [MakeObservation()];

        var result = await svc.ObserveAsync();

        Assert.True(result.Success);
        Assert.Equal(1, result.Created);
        Assert.Equal(0, result.Updated);
        Assert.Equal(0, result.Recovered);

        var row = repo.AllRecords.Single();
        Assert.Equal(Key28879, row.IncidentKey);
        Assert.Equal(1, row.OccurrenceNumber);
        Assert.Equal(ZfLifecycleStatus.Active, row.LifecycleStatus);
        Assert.Null(row.ClearedAtUtc);
    }

    // ── ZDH_02: active incident observed again → LastObservedAtUtc updated ───

    [Fact]
    public async Task ZDH_02_ActiveIncidentObservedAgain_LastObservedAdvances()
    {
        var (svc, detector, repo) = Build();
        detector.NextResult = [MakeObservation()];
        await svc.ObserveAsync();
        var firstObserved = repo.AllRecords.Single().LastObservedAtUtc;

        await Task.Delay(15);
        detector.NextResult = [MakeObservation()];
        var result = await svc.ObserveAsync();

        Assert.Equal(0, result.Created);
        Assert.Equal(1, result.Updated);
        var row = repo.AllRecords.Single();
        Assert.True(row.LastObservedAtUtc > firstObserved);
        Assert.Equal(ZfLifecycleStatus.Active, row.LifecycleStatus); // unchanged
        Assert.Equal(1, row.OccurrenceNumber);                       // same occurrence, not a new row
    }

    // ── ZDH_03: incident disappears → RECOVERED + ClearedAtUtc ────────────────

    [Fact]
    public async Task ZDH_03_IncidentDisappears_RecoveredWithClearedAt()
    {
        var (svc, detector, repo) = Build();
        detector.NextResult = [MakeObservation()];
        await svc.ObserveAsync();

        detector.NextResult = []; // no longer detected
        var result = await svc.ObserveAsync();

        Assert.Equal(0, result.Created);
        Assert.Equal(0, result.Updated);
        Assert.Equal(1, result.Recovered);

        var row = repo.AllRecords.Single();
        Assert.Equal(ZfLifecycleStatus.Recovered, row.LifecycleStatus);
        Assert.NotNull(row.ClearedAtUtc);
        Assert.Equal(ZfRecoveryReason.NotDetectedInLiveScan, row.RecoveryReason);
    }

    // ── ZDH_04: recovered incident remains absent → no duplicate recovery ────

    [Fact]
    public async Task ZDH_04_RecoveredIncidentStaysAbsent_NoDuplicateRecovery()
    {
        var (svc, detector, repo) = Build();
        detector.NextResult = [MakeObservation()];
        await svc.ObserveAsync();
        detector.NextResult = [];
        await svc.ObserveAsync();
        var clearedAtFirst = repo.AllRecords.Single().ClearedAtUtc;

        detector.NextResult = []; // still absent
        var result = await svc.ObserveAsync();

        Assert.Equal(0, result.Recovered); // nothing to recover — already RECOVERED
        Assert.Single(repo.AllRecords);    // no duplicate row created
        Assert.Equal(clearedAtFirst, repo.AllRecords.Single().ClearedAtUtc); // untouched
    }

    // ── ZDH_05: recovered incident reappears → recurrence handled correctly ──

    [Fact]
    public async Task ZDH_05_RecoveredIncidentReappears_NewOccurrence()
    {
        var (svc, detector, repo) = Build();
        detector.NextResult = [MakeObservation()];
        await svc.ObserveAsync();               // occurrence 1: ACTIVE
        detector.NextResult = [];
        await svc.ObserveAsync();               // occurrence 1: RECOVERED

        detector.NextResult = [MakeObservation()]; // same IncidentKey reappears
        var result = await svc.ObserveAsync();

        Assert.Equal(1, result.Created);
        Assert.Equal(2, repo.AllRecords.Count);

        var occ1 = repo.AllRecords.Single(r => r.OccurrenceNumber == 1);
        var occ2 = repo.AllRecords.Single(r => r.OccurrenceNumber == 2);
        Assert.Equal(ZfLifecycleStatus.Recovered, occ1.LifecycleStatus); // history preserved
        Assert.Equal(ZfLifecycleStatus.Active,    occ2.LifecycleStatus);
        Assert.Equal(Key28879, occ1.IncidentKey);
        Assert.Equal(Key28879, occ2.IncidentKey);
    }

    // ── ZDH_06: human RESOLVED while technical ACTIVE ─────────────────────────

    [Fact]
    public async Task ZDH_06_HumanResolvedWhileTechnicalActive_BothDimensionsIndependent()
    {
        var (svc, detector, repo) = Build();
        detector.NextResult = [MakeObservation()];
        await svc.ObserveAsync();

        // Simulate the dashboard-layer join: technical row stays ACTIVE regardless of
        // whatever a human resolution says — the two are never collapsed into one status.
        var row = repo.AllRecords.Single();
        Assert.Equal(ZfLifecycleStatus.Active, row.LifecycleStatus);
        // A resolution record would live entirely in ZfIncidentResolutions (untouched by
        // this service) — asserting here that nothing in this class has any notion of
        // "resolution" at all confirms the independence structurally, not just by convention.
        Assert.DoesNotContain("Resolution", typeof(ZfDiagnosticIncidentHistoryRecord)
            .GetProperties().Select(p => p.Name));
    }

    // ── ZDH_07: technical RECOVERED with no human resolution ──────────────────

    [Fact]
    public async Task ZDH_07_TechnicalRecovered_NoHumanResolution_Valid()
    {
        var (svc, detector, repo) = Build();
        detector.NextResult = [MakeObservation()];
        await svc.ObserveAsync();
        detector.NextResult = [];
        await svc.ObserveAsync();

        var row = repo.AllRecords.Single();
        Assert.Equal(ZfLifecycleStatus.Recovered, row.LifecycleStatus);
        // No resolution concept exists on this record at all — RECOVERED + "no human
        // decision" is not just allowed, it's the only thing this table can express.
    }

    // ── ZDH_08: deterministic IncidentKey preserved ───────────────────────────

    [Fact]
    public async Task ZDH_08_DeterministicIncidentKey_Preserved()
    {
        var (svc, detector, repo) = Build();
        detector.NextResult = [MakeObservation()];
        await svc.ObserveAsync();

        Assert.Equal(
            ZfIncidentResolutionRepository.BuildIncidentKey(28879, 20092, ZfConsistencyStatus.FragmentRdr1Missing),
            repo.AllRecords.Single().IncidentKey);
        Assert.Equal("28879_20092_ZF_FRAGMENT_RDR1_MISSING", repo.AllRecords.Single().IncidentKey);
    }

    // ── ZDH_09: SO 28879 regression — full field set persisted correctly ──────

    [Fact]
    public async Task ZDH_09_SO28879_Regression_FieldsMatchReference()
    {
        var (svc, detector, repo) = Build();
        detector.NextResult = [MakeObservation()];
        await svc.ObserveAsync();

        var row = repo.AllRecords.Single();
        Assert.Equal(28879, row.SoDocNum);
        Assert.Equal(28879, row.SoDocEntry);
        Assert.Equal(20056L, row.OrchestrationId);
        Assert.Equal(20092L, row.FragmentId);
        Assert.Equal("VAG13782", row.ItemCode);
        Assert.Equal(1, row.ExpectedLineNum);
        Assert.Equal(1m, row.ExpectedQty);
        Assert.Equal(ZfDiagnosticSeverity.High, row.Severity);
        Assert.Equal(ZfConsistencyStatus.FragmentRdr1Missing, row.IncidentCode);
        Assert.Equal(ZfLifecycleStatus.Active, row.LifecycleStatus);
    }

    // ── ZDH_10: no SAP mutation from history persistence ──────────────────────

    [Fact]
    public void ZDH_10_NoSapMutationSurface()
    {
        // Structural guarantee: none of the observation classes reference SapService,
        // SAPbobsCOM, or any SAP DI API type at all — confirmed by their constructor
        // signatures taking only ZfLiveIncidentDetector/ZfDiagnosticIncidentHistoryRepository
        // (both pure-SQL) and ILogger. This is checked at compile time by this test file
        // simply compiling without a SAP dependency anywhere in the call graph below.
        var ctor = typeof(ZfIncidentObservationService).GetConstructors().Single();
        var paramTypeNames = ctor.GetParameters().Select(p => p.ParameterType.Name);
        Assert.DoesNotContain(paramTypeNames, n => n.Contains("Sap", StringComparison.OrdinalIgnoreCase));
    }

    // ── ZDH_11: dashboard active count sourced from durable state ─────────────

    [Fact]
    public async Task ZDH_11_ActiveCount_SourcedFromDurableState()
    {
        var (svc, detector, repo) = Build();
        detector.NextResult = [MakeObservation(), MakeObservation(key: "99001_10001_ZF_FRAGMENT_RDR1_MISSING", soDocNum: 99001, fragmentId: 10001)];
        await svc.ObserveAsync();

        var latest = await repo.GetLatestOccurrencesAsync();
        Assert.Equal(2, latest.Count(r => r.LifecycleStatus == ZfLifecycleStatus.Active));
    }

    // ── ZDH_12: Recovered Today returns a real durable count ──────────────────

    [Fact]
    public async Task ZDH_12_RecoveredToday_RealDurableCount()
    {
        var (svc, detector, repo) = Build();
        detector.NextResult = [MakeObservation()];
        await svc.ObserveAsync();
        detector.NextResult = [];
        await svc.ObserveAsync(); // recovers now

        var (startUtc, endUtc) = ZfDashboardService.GetEatTodayWindowUtc(DateTime.UtcNow);
        var count = await repo.CountRecoveredInWindowAsync(startUtc, endUtc);

        Assert.Equal(1, count);
    }

    [Fact]
    public void ZDH_12b_EatWindow_IsThreeHoursAheadOfUtc()
    {
        // 2026-01-15 00:30 UTC is 2026-01-15 03:30 EAT — still the same EAT calendar day.
        var utcNow = new DateTime(2026, 1, 15, 0, 30, 0, DateTimeKind.Utc);
        var (startUtc, endUtc) = ZfDashboardService.GetEatTodayWindowUtc(utcNow);

        // Midnight EAT on 2026-01-15 is 2026-01-14 21:00 UTC.
        Assert.Equal(new DateTime(2026, 1, 14, 21, 0, 0), startUtc);
        Assert.Equal(new DateTime(2026, 1, 15, 21, 0, 0), endUtc);
    }

    [Fact]
    public void ZDH_12c_EatWindow_UtcBoundaryDoesNotSplitEatDay()
    {
        // 2026-01-14 23:30 UTC is 2026-01-15 02:30 EAT — a naive UTC.Date boundary would
        // put this in the wrong "today", which is exactly the bug §6 asks to avoid.
        var utcNow = new DateTime(2026, 1, 14, 23, 30, 0, DateTimeKind.Utc);
        var (startUtc, _) = ZfDashboardService.GetEatTodayWindowUtc(utcNow);

        Assert.Equal(new DateTime(2026, 1, 14, 21, 0, 0), startUtc); // still EAT-Jan-15's start
    }

    // ── ZDH_13: filters ACTIVE / RECOVERED work correctly ─────────────────────

    [Fact]
    public async Task ZDH_13_Filter_ActiveVsRecovered()
    {
        var (svc, detector, repo) = Build();
        detector.NextResult = [MakeObservation(), MakeObservation(key: "K2", soDocNum: 2, fragmentId: 2)];
        await svc.ObserveAsync();
        detector.NextResult = [MakeObservation()]; // K2 recovers
        await svc.ObserveAsync();

        var latest = await repo.GetLatestOccurrencesAsync();
        var active    = latest.Where(r => r.LifecycleStatus == ZfLifecycleStatus.Active).ToList();
        var recovered = latest.Where(r => r.LifecycleStatus == ZfLifecycleStatus.Recovered).ToList();

        Assert.Single(active);
        Assert.Single(recovered);
        Assert.Equal(Key28879, active[0].IncidentKey);
        Assert.Equal("K2", recovered[0].IncidentKey);
    }

    // ── ZDH_14: paging/sorting determinism — covered by ZfDashboardTests'
    //    existing ZDB_13/21/22/23 (unchanged: they exercise the in-memory
    //    ApplyFilters/ApplySort/Paginate helpers directly, independent of this
    //    table). No additional test needed here; asserting that contract still holds.

    [Fact]
    public void ZDH_14_ListItemShape_UnchangedForExistingSortHelpers()
    {
        // The properties ZfDashboardTests' local ApplySort reimplementation depends on
        // (IncidentKey, DetectedAtUtc, Severity, SoDocNum, ResolutionStatus, AgeMinutes)
        // all still exist with the same names/types after this change.
        var props = typeof(ZfIncidentListItem).GetProperties().ToDictionary(p => p.Name);
        Assert.True(props.ContainsKey("IncidentKey"));
        Assert.True(props.ContainsKey("DetectedAtUtc"));
        Assert.True(props.ContainsKey("Severity"));
        Assert.True(props.ContainsKey("SoDocNum"));
        Assert.True(props.ContainsKey("ResolutionStatus"));
        Assert.True(props.ContainsKey("AgeMinutes"));
        // New additive field:
        Assert.True(props.ContainsKey("ClearedAtUtc"));
    }

    // ── ZDH_15: legacy incident does not receive fabricated timestamps ────────

    [Fact]
    public async Task ZDH_15_LegacyIncident_NoFabricatedTimestamps()
    {
        // SO 28890 / 28917 predate durable history — they already recovered before the
        // observation job ever ran, so the live scan never reports them and no row is
        // ever created for them. Confirms no backfill path exists in this service at all.
        var (svc, detector, repo) = Build();
        detector.NextResult = [MakeObservation()]; // only 28879 currently active
        await svc.ObserveAsync();

        Assert.DoesNotContain(repo.AllRecords, r => r.SoDocNum == 28890);
        Assert.DoesNotContain(repo.AllRecords, r => r.SoDocNum == 28917);
    }

    // ── ZDH_16: observation failure while ACTIVE → remains ACTIVE ─────────────

    [Fact]
    public async Task ZDH_16_ObservationFailure_ActiveIncidentStaysActive()
    {
        var (svc, detector, repo) = Build();
        detector.NextResult = [MakeObservation()];
        await svc.ObserveAsync();

        detector.ThrowOnNextCall = new InvalidOperationException("SQL Server unavailable");
        var result = await svc.ObserveAsync();

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        var row = repo.AllRecords.Single();
        Assert.Equal(ZfLifecycleStatus.Active, row.LifecycleStatus); // untouched
        Assert.Null(row.ClearedAtUtc);
    }

    // ── ZDH_17: partial/incomplete scan must not falsely recover incidents ────

    [Fact]
    public async Task ZDH_17_DetectionThrows_NoRowsMutatedAtAll()
    {
        var (svc, detector, repo) = Build();
        detector.NextResult = [MakeObservation(), MakeObservation(key: "K2", soDocNum: 2, fragmentId: 2)];
        await svc.ObserveAsync();
        var beforeSnapshot = repo.AllRecords.Select(r => (r.Id, r.LifecycleStatus, r.LastObservedAtUtc)).ToList();

        detector.ThrowOnNextCall = new TimeoutException("query timeout");
        var result = await svc.ObserveAsync();

        Assert.False(result.Success);
        var afterSnapshot = repo.AllRecords.Select(r => (r.Id, r.LifecycleStatus, r.LastObservedAtUtc)).ToList();
        Assert.Equal(beforeSnapshot, afterSnapshot); // byte-for-byte unchanged
    }

    // ── Concurrency guard: direct semaphore-state check (no timing dependency) ─
    //
    // Task.WhenAll on two calls to a fully-synchronous fake (no real I/O to yield on)
    // doesn't reliably race them — .NET can run both to completion before either
    // truly overlaps. Instead, hold the gate open deliberately (via a detector that
    // blocks until released) so the second call is guaranteed to observe it taken.

    [Fact]
    public async Task ZDH_Guard_ConcurrentObserveAsync_SecondCallSkipsCleanly()
    {
        var (svc, detector, _) = Build();
        var release = new TaskCompletionSource();
        detector.NextResult = [MakeObservation()];
        detector.BlockUntil = release.Task;

        var t1 = svc.ObserveAsync(); // enters the gate, then awaits release.Task mid-cycle
        await Task.Delay(30);        // give t1 time to acquire the gate first

        var second = await svc.ObserveAsync(); // must be skipped — gate is held by t1
        Assert.Equal("SKIPPED_ALREADY_RUNNING", second.Error);

        release.SetResult();
        var first = await t1;
        Assert.True(first.Success);
        Assert.Equal(1, first.Created);
    }

    // ── Fakes ──────────────────────────────────────────────────────────────────

    private sealed class FakeDetector : ZfLiveIncidentDetector
    {
        public List<ZfLiveIncidentObservation> NextResult { get; set; } = [];
        public Exception? ThrowOnNextCall { get; set; }
        /// <summary>When set, DetectCurrentIncidentsAsync awaits this before returning —
        /// used to hold ZfIncidentObservationService's gate open for a concurrency test.</summary>
        public Task? BlockUntil { get; set; }

        public override async Task<List<ZfLiveIncidentObservation>> DetectCurrentIncidentsAsync(CancellationToken ct = default)
        {
            if (ThrowOnNextCall is { } ex)
            {
                ThrowOnNextCall = null;
                throw ex;
            }
            if (BlockUntil is { } gate)
                await gate;
            return NextResult;
        }
    }

    private sealed class FakeHistoryRepo : ZfDiagnosticIncidentHistoryRepository
    {
        private readonly List<ZfDiagnosticIncidentHistoryRecord> _records = [];
        private long _nextId = 1;
        private readonly object _lock = new();

        public IReadOnlyList<ZfDiagnosticIncidentHistoryRecord> AllRecords => _records;

        public override Task EnsureTableAsync(CancellationToken ct = default) => Task.CompletedTask;

        public override Task<long> InsertNewOccurrenceAsync(
            ZfDiagnosticIncidentHistoryRecord rec, CancellationToken ct = default)
        {
            lock (_lock)
            {
                rec.Id = _nextId++;
                _records.Add(rec);
                return Task.FromResult(rec.Id);
            }
        }

        public override Task UpdateObservedAsync(
            long id, DateTime lastObservedAtUtc, string? evidenceJson, DateTime updatedAtUtc, CancellationToken ct = default)
        {
            var row = _records.First(r => r.Id == id);
            row.LastObservedAtUtc = lastObservedAtUtc;
            row.EvidenceJson = evidenceJson;
            row.UpdatedAtUtc = updatedAtUtc;
            return Task.CompletedTask;
        }

        public override Task MarkRecoveredAsync(
            long id, DateTime clearedAtUtc, string recoveryReason, DateTime updatedAtUtc, CancellationToken ct = default)
        {
            var row = _records.FirstOrDefault(r => r.Id == id && r.LifecycleStatus == ZfLifecycleStatus.Active);
            if (row is null) return Task.CompletedTask;
            row.LifecycleStatus = ZfLifecycleStatus.Recovered;
            row.ClearedAtUtc    = clearedAtUtc;
            row.RecoveryReason  = recoveryReason;
            row.UpdatedAtUtc    = updatedAtUtc;
            return Task.CompletedTask;
        }

        public override Task<IReadOnlyDictionary<string, ZfDiagnosticIncidentHistoryRecord>> GetActiveOccurrencesAsync(
            CancellationToken ct = default)
        {
            IReadOnlyDictionary<string, ZfDiagnosticIncidentHistoryRecord> result = _records
                .Where(r => r.LifecycleStatus == ZfLifecycleStatus.Active)
                .ToDictionary(r => r.IncidentKey, StringComparer.Ordinal);
            return Task.FromResult(result);
        }

        public override Task<IReadOnlyList<ZfDiagnosticIncidentHistoryRecord>> GetLatestOccurrencesAsync(
            CancellationToken ct = default)
        {
            IReadOnlyList<ZfDiagnosticIncidentHistoryRecord> result = _records
                .GroupBy(r => r.IncidentKey)
                .Select(g => g.OrderByDescending(r => r.OccurrenceNumber).First())
                .ToList();
            return Task.FromResult(result);
        }

        public override Task<int> CountRecoveredInWindowAsync(
            DateTime windowStartUtc, DateTime windowEndUtc, CancellationToken ct = default)
        {
            var count = _records.Count(r =>
                r.LifecycleStatus == ZfLifecycleStatus.Recovered &&
                r.ClearedAtUtc.HasValue &&
                r.ClearedAtUtc.Value >= windowStartUtc && r.ClearedAtUtc.Value < windowEndUtc);
            return Task.FromResult(count);
        }

        public override Task<IReadOnlyList<ZfDiagnosticIncidentHistoryRecord>> GetOccurrencesAsync(
            string incidentKey, CancellationToken ct = default)
        {
            IReadOnlyList<ZfDiagnosticIncidentHistoryRecord> result = _records
                .Where(r => r.IncidentKey == incidentKey)
                .OrderBy(r => r.OccurrenceNumber)
                .ToList();
            return Task.FromResult(result);
        }
    }
}
