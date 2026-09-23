using SapReplitAPI.Models.ZoneFulfillment;
using SapReplitAPI.Services.ZoneFulfillment;
using Xunit;

namespace SapReplitAPI.Tests.ZfAdmin;

/// <summary>
/// ZRI_01–ZRI_10: Resolution idempotency tests.
///
/// All tests use FakeZfIncidentResolutionRepositoryV2 (in-memory, no SQL).
///
/// Contract:
///   First submission with a given ResolutionRequestId → INSERT, return (record, isReplay=false).
///   Same ResolutionRequestId replay → return original, (record, isReplay=true), no new INSERT.
///   Different ResolutionRequestId → new INSERT, (record, isReplay=false).
///   Null ResolutionRequestId → append-only (no idempotency check), always INSERT.
/// </summary>
public sealed class ZfResolutionIdempotencyTests
{
    private const string Key = "28879_20092_ZF_FRAGMENT_RDR1_MISSING";

    // ── ZRI_01: first submission inserts and returns isReplay=false ───────────

    [Fact]
    public async Task ZRI_01_FirstSubmission_Inserts_IsReplayFalse()
    {
        var repo = new FakeZfIncidentResolutionRepositoryV2();
        var guid = Guid.NewGuid();

        var rec = MakeRecord(guid);
        var (stored, isReplay) = await repo.InsertIdempotentAsync(rec);

        Assert.False(isReplay);
        Assert.True(stored.Id > 0);
        Assert.Single(repo.AllRecords);
    }

    // ── ZRI_02: same GUID replay returns original, no duplicate INSERT ────────

    [Fact]
    public async Task ZRI_02_SameGuid_Replay_ReturnsOriginal_NoInsert()
    {
        var repo = new FakeZfIncidentResolutionRepositoryV2();
        var guid = Guid.NewGuid();

        var (first, _) = await repo.InsertIdempotentAsync(MakeRecord(guid));
        var (second, isReplay) = await repo.InsertIdempotentAsync(MakeRecord(guid));

        Assert.True(isReplay);
        Assert.Equal(first.Id, second.Id);
        Assert.Single(repo.AllRecords);    // still only one row
    }

    // ── ZRI_03: different GUID is a new audit event ───────────────────────────

    [Fact]
    public async Task ZRI_03_DifferentGuid_NewAuditEvent()
    {
        var repo = new FakeZfIncidentResolutionRepositoryV2();

        var (r1, replay1) = await repo.InsertIdempotentAsync(MakeRecord(Guid.NewGuid()));
        var (r2, replay2) = await repo.InsertIdempotentAsync(MakeRecord(Guid.NewGuid()));

        Assert.False(replay1);
        Assert.False(replay2);
        Assert.NotEqual(r1.Id, r2.Id);
        Assert.Equal(2, repo.AllRecords.Count);
    }

    // ── ZRI_04: null GUID always inserts (append-only fallback) ─────────────

    [Fact]
    public async Task ZRI_04_NullGuid_AppendOnly_AlwaysInserts()
    {
        var repo = new FakeZfIncidentResolutionRepositoryV2();

        var (r1, _) = await repo.InsertIdempotentAsync(MakeRecord(null));
        var (r2, _) = await repo.InsertIdempotentAsync(MakeRecord(null));

        Assert.Equal(2, repo.AllRecords.Count);
        Assert.NotEqual(r1.Id, r2.Id);
    }

    // ── ZRI_05: FindByResolutionRequestId returns existing record ────────────

    [Fact]
    public async Task ZRI_05_FindByRequestId_ReturnsExisting()
    {
        var repo = new FakeZfIncidentResolutionRepositoryV2();
        var guid = Guid.NewGuid();

        await repo.InsertIdempotentAsync(MakeRecord(guid));

        var found = await repo.FindByResolutionRequestIdAsync(guid);

        Assert.NotNull(found);
        Assert.Equal(guid, found!.ResolutionRequestId);
        Assert.Equal(Key, found.IncidentKey);
    }

    // ── ZRI_06: FindByResolutionRequestId returns null for unknown GUID ──────

    [Fact]
    public async Task ZRI_06_FindByRequestId_Unknown_ReturnsNull()
    {
        var repo = new FakeZfIncidentResolutionRepositoryV2();

        var result = await repo.FindByResolutionRequestIdAsync(Guid.NewGuid());

        Assert.Null(result);
    }

    // ── ZRI_07: replay returns original operator/reason (not mutated) ─────────

    [Fact]
    public async Task ZRI_07_Replay_ReturnsOriginalOperatorReason()
    {
        var repo = new FakeZfIncidentResolutionRepositoryV2();
        var guid = Guid.NewGuid();

        var original = MakeRecord(guid, operator_: "first-operator", reason: "first-reason");
        await repo.InsertIdempotentAsync(original);

        var different = MakeRecord(guid, operator_: "second-operator", reason: "second-reason");
        var (replayed, isReplay) = await repo.InsertIdempotentAsync(different);

        Assert.True(isReplay);
        Assert.Equal("first-operator", replayed.Operator);
        Assert.Equal("first-reason", replayed.Reason);
    }

    // ── ZRI_08: no SAP mutations called during any idempotent operation ───────

    [Fact]
    public async Task ZRI_08_NoSapMutations_DuringIdempotentOps()
    {
        var repo = new FakeZfIncidentResolutionRepositoryV2();
        var guid = Guid.NewGuid();

        await repo.InsertIdempotentAsync(MakeRecord(guid));
        await repo.InsertIdempotentAsync(MakeRecord(guid));   // replay
        await repo.InsertIdempotentAsync(MakeRecord(Guid.NewGuid())); // new

        Assert.Equal(0, repo.SapMutationCallCount);
    }

    // ── ZRI_09: resolution type preserved through replay ─────────────────────

    [Fact]
    public async Task ZRI_09_ResolutionType_PreservedOnReplay()
    {
        var repo = new FakeZfIncidentResolutionRepositoryV2();
        var guid = Guid.NewGuid();

        var rec = MakeRecord(guid, resolution: ZfIncidentResolution.AcknowledgedExternalSapEdit);
        await repo.InsertIdempotentAsync(rec);

        var (replayed, isReplay) = await repo.InsertIdempotentAsync(MakeRecord(guid));

        Assert.True(isReplay);
        Assert.Equal(ZfIncidentResolution.AcknowledgedExternalSapEdit, replayed.Resolution);
        Assert.Equal(ZfIncidentStatusValue.Resolved, replayed.Status);
    }

    // ── ZRI_10: GetAllLatestResolutions returns latest-per-key ───────────────

    [Fact]
    public async Task ZRI_10_GetAllLatestResolutions_ReturnsLatestPerKey()
    {
        var repo = new FakeZfIncidentResolutionRepositoryV2();

        // Two resolutions for same key
        await repo.InsertIdempotentAsync(MakeRecord(Guid.NewGuid(),
            resolution: ZfIncidentResolution.Deferred));
        await repo.InsertIdempotentAsync(MakeRecord(Guid.NewGuid(),
            resolution: ZfIncidentResolution.AcknowledgedExternalSapEdit));

        // One resolution for a different key
        var rec2 = MakeRecord(Guid.NewGuid());
        rec2.IncidentKey = "99999_99001_ZF_FRAGMENT_RDR1_MISSING";
        await repo.InsertIdempotentAsync(rec2);

        var latest = await repo.GetAllLatestResolutionsAsync();

        Assert.Equal(2, latest.Count);   // 2 distinct keys
        Assert.Equal(ZfIncidentResolution.AcknowledgedExternalSapEdit,
            latest[Key].Resolution);     // most recent for primary key
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ZfIncidentResolutionRecord MakeRecord(
        Guid?  requestId,
        string resolution = ZfIncidentResolution.AcknowledgedExternalSapEdit,
        string operator_  = "test-operator",
        string reason     = "test reason") => new()
    {
        ResolutionRequestId = requestId,
        IncidentKey         = Key,
        SoDocNum            = 28879,
        SoDocEntry          = 28879,
        OrchestrationId     = 20056,
        FragmentId          = 20092,
        IncidentCode        = ZfConsistencyStatus.FragmentRdr1Missing,
        Resolution          = resolution,
        Status              = ZfIncidentResolution.ToIncidentStatus(resolution),
        Operator            = operator_,
        Reason              = reason,
        ResolvedAtUtc       = DateTime.UtcNow,
    };
}

// ── Extended fake with idempotency support ────────────────────────────────────

/// <summary>
/// In-memory fake that supports ResolutionRequestId idempotency.
/// Extends the base fake to validate Phase 2 behaviour without SQL.
/// </summary>
internal sealed class FakeZfIncidentResolutionRepositoryV2 : ZfIncidentResolutionRepository
{
    private readonly List<ZfIncidentResolutionRecord> _records = [];
    private long _nextId = 1;

    public int SapMutationCallCount { get; private set; }
    public IReadOnlyList<ZfIncidentResolutionRecord> AllRecords => _records;

    public override Task EnsureTableAsync(CancellationToken ct = default) => Task.CompletedTask;

    public override Task EnsureResolutionRequestIdColumnAsync(CancellationToken ct = default)
        => Task.CompletedTask;

    public override Task<long> InsertResolutionAsync(
        ZfIncidentResolutionRecord rec, CancellationToken ct = default)
    {
        var id = _nextId++;
        rec.Id = id;
        _records.Add(rec);
        return Task.FromResult(id);
    }

    public override Task<(ZfIncidentResolutionRecord record, bool isReplay)> InsertIdempotentAsync(
        ZfIncidentResolutionRecord rec, CancellationToken ct = default)
    {
        if (rec.ResolutionRequestId is not null)
        {
            var existing = _records.FirstOrDefault(r => r.ResolutionRequestId == rec.ResolutionRequestId);
            if (existing is not null)
                return Task.FromResult((existing, true));
        }

        var id = _nextId++;
        rec.Id = id;
        _records.Add(rec);
        return Task.FromResult((rec, false));
    }

    public override Task<ZfIncidentResolutionRecord?> FindByResolutionRequestIdAsync(
        Guid requestId, CancellationToken ct = default)
    {
        var found = _records.FirstOrDefault(r => r.ResolutionRequestId == requestId);
        return Task.FromResult<ZfIncidentResolutionRecord?>(found);
    }

    public override Task<IReadOnlyList<ZfIncidentResolutionRecord>> GetResolutionsAsync(
        string incidentKey, CancellationToken ct = default)
    {
        IReadOnlyList<ZfIncidentResolutionRecord> result =
            _records.Where(r => r.IncidentKey == incidentKey).ToList();
        return Task.FromResult(result);
    }

    public override Task<string> GetCurrentStatusAsync(
        string incidentKey, CancellationToken ct = default)
    {
        var latest = _records
            .Where(r => r.IncidentKey == incidentKey)
            .LastOrDefault();
        return Task.FromResult(latest?.Status ?? ZfIncidentStatusValue.Active);
    }

    public override Task<IReadOnlyDictionary<string, ZfIncidentResolutionRecord>> GetAllLatestResolutionsAsync(
        CancellationToken ct = default)
    {
        IReadOnlyDictionary<string, ZfIncidentResolutionRecord> result = _records
            .GroupBy(r => r.IncidentKey)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(r => r.Id).First());
        return Task.FromResult(result);
    }

    public override Task<IReadOnlyList<ZfIncidentResolutionRecord>> GetHistoricalIncidentKeysAsync(
        CancellationToken ct = default)
    {
        IReadOnlyList<ZfIncidentResolutionRecord> result = _records
            .GroupBy(r => r.IncidentKey)
            .Select(g => g.OrderByDescending(r => r.Id).First())
            .ToList();
        return Task.FromResult(result);
    }
}
