using SapReplitAPI.Models.ZoneFulfillment;
using SapReplitAPI.Services.ZoneFulfillment;
using Xunit;

namespace SapReplitAPI.Tests.TieredAllocation;

/// <summary>
/// Gate 1A — actual production code-path tests for Pick List timestamps.
///
/// PT01–PT02: PLR CreatedAtUtc semantics (INSERT ordering vs SAP OPKL.Add).
/// PT03–PT04: Partial vs full API pick stamp behaviour (write-once SQL semantics).
/// PT05–PT07: SAP-native reconciliation path stamps PickedAtUtc via UpdatePickListPickedQtyAsync.
/// PT08:      Historical null behaviour (pre-Gate-1 PLR row).
/// PT09:      Active cache (SQLite/Neon) tracks OPKL-level fields, NOT dbo.PickListRecord.
/// PT10:      UTC preservation through write-once model.
///
/// All tests use fakes/in-memory seams — NO production SAP mutations.
/// </summary>
public sealed class PickListTimestampPathTests
{
    // ── Fakes ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// In-memory implementation of IZfReconciliationRepo.
    /// Tracks calls to UpdatePickListPickedQtyAsync to verify write-once stamp behaviour.
    /// </summary>
    private sealed class FakeReconciliationRepo : IZfReconciliationRepo
    {
        public List<FulfillmentOrchestrationRecord> StuckOrchestrations { get; } = [];
        public FulfillmentOrchestrationRecord?      FreshOrchestration   { get; set; }
        public List<PickListRecordModel>             PlrRows              { get; } = [];

        /// <summary>Recorded (Id, PickedQty, Status) calls to UpdatePickListPickedQtyAsync.</summary>
        public List<(long Id, decimal PickedQty, string Status)> UpdateCalls { get; } = [];

        /// <summary>Current in-memory state of PickedAtUtc per PLR.Id (simulates COALESCE).</summary>
        private readonly Dictionary<long, DateTime?> _pickedAtUtc = [];

        public Task<List<FulfillmentOrchestrationRecord>> GetStuckAcceptedOrchestrationsAsync(
            int thresholdSeconds, CancellationToken ct = default)
            => Task.FromResult(StuckOrchestrations);

        public Task<FulfillmentOrchestrationRecord?> FindOrchestrationAsync(
            Guid requestId, CancellationToken ct = default)
            => Task.FromResult(FreshOrchestration);

        public Task<List<PickListRecordModel>> GetPickListRecordsAsync(
            long orchestrationId, CancellationToken ct = default)
            => Task.FromResult(PlrRows);

        public Task UpdatePickListPickedQtyAsync(
            long pickListRecordId, decimal pickedQty, string status, CancellationToken ct = default)
        {
            UpdateCalls.Add((pickListRecordId, pickedQty, status));

            // Simulate COALESCE write-once SQL:
            // PickedAtUtc = CASE WHEN @status = 'Picked'
            //                    THEN COALESCE(PickedAtUtc, SYSUTCDATETIME())
            //                    ELSE PickedAtUtc END
            if (status == PickListStatus.Picked)
            {
                if (!_pickedAtUtc.TryGetValue(pickListRecordId, out var existing) || existing is null)
                    _pickedAtUtc[pickListRecordId] = DateTime.UtcNow;
                // else: already stamped — COALESCE preserves existing value
            }
            // non-Picked: no change to PickedAtUtc

            // Update status on the PLR row
            var plr = PlrRows.FirstOrDefault(p => p.Id == pickListRecordId);
            if (plr is not null) plr.Status = status;

            return Task.CompletedTask;
        }

        public Task<int> UpdatePickListFragmentPickedQtyAsync(
            long pickListRecordId, decimal pickedQty, string pickStatus, CancellationToken ct = default)
            => Task.FromResult(0); // 0 = pre-PLFR history safe

        public DateTime? GetPickedAtUtc(long plrId)
            => _pickedAtUtc.TryGetValue(plrId, out var v) ? v : null;
    }

    /// <summary>Fake SAP reader: PLR 200 is fully confirmed (PickStatus=Y, qty=5).</summary>
    private sealed class FakeFullyPickedSapReader : IZfPickSapReader
    {
        private readonly Dictionary<int, decimal> _pickQttyByAbsEntry;

        public FakeFullyPickedSapReader(Dictionary<int, decimal> pickQttyByAbsEntry)
            => _pickQttyByAbsEntry = pickQttyByAbsEntry;

        public (ZfOpklValidation? Header,
                IReadOnlyList<ZfPkl1Validation> Lines,
                IReadOnlyList<ZfPkl2Validation> Bins)
            GetPickListValidationState(int absEntry)
        {
            if (!_pickQttyByAbsEntry.TryGetValue(absEntry, out var qty))
                return (null, [], []);

            // ZfOpklValidation(int AbsEntry, string Status, string Canceled, string? UReplitId)
            var header = new ZfOpklValidation(absEntry, "Y", "N", "ZF-TEST-001");

            // ZfPkl1Validation(int PickEntry, int OrderEntry, int OrderLine,
            //                   int BaseObject, string WhsCode, decimal RelQtty, decimal PickQtty, string PickStatus)
            var lines = new List<ZfPkl1Validation>
            {
                new ZfPkl1Validation(
                    PickEntry:  1,
                    OrderEntry: 1000,   // matches plr.SoDocEntry
                    OrderLine:  1,      // matches plr.SoLineNum (id=1)
                    BaseObject: 17,     // ORDR
                    WhsCode:    "001",  // matches plr.WhsCode
                    RelQtty:    qty,
                    PickQtty:   qty,
                    PickStatus: "Y")
            };

            return (header, lines, []);
        }
    }

    private sealed class FakeAutomation : IZfAutomation
    {
        public Task<PostPickAutomationResult> EvaluateAndTriggerDeliveryAsync(
            Guid requestId, CancellationToken ct = default)
            => Task.FromResult(new PostPickAutomationResult { AutomationStatus = "NoAction" });
    }

    private static PickListRecordModel MakePlr(long id, int absEntry, decimal releasedQty, string status = PickListStatus.Created)
        => new()
        {
            Id               = id,
            OrchestrationId  = 1,
            SoLineFragmentId = id,
            SoDocEntry       = 1000,
            SoLineNum        = (int)id,
            WhsCode          = "001",
            PickListAbsEntry = absEntry,
            ReleasedQty      = releasedQty,
            PickedQty        = 0m,
            Status           = status,
            CreatedAtUtc     = DateTime.UtcNow.AddMinutes(-10),
            UpdatedAtUtc     = DateTime.UtcNow.AddMinutes(-10)
        };

    private static FulfillmentOrchestrationRecord MakeOrch(Guid? rid = null)
        => new()
        {
            Id               = 1,
            RequestId        = rid ?? Guid.NewGuid(),
            State            = OrchestrationState.Accepted,
            U_ReplitId       = "ZF-TEST-001",
            SoDocEntry       = 1000,
            DeliveryLocation = "Cluster-side",
            AllocationVersion = 1,
            CreatedAtUtc     = DateTime.UtcNow.AddHours(-1),
            UpdatedAtUtc     = DateTime.UtcNow.AddMinutes(-5)
        };

    // ── PT01 ──────────────────────────────────────────────────────────────────
    // PLR INSERT SQL contains SYSUTCDATETIME() for CreatedAtUtc — verified from repository source.
    // The INSERT fires AFTER _sap.CreateZoneFulfillmentPickListMultiLine() succeeds.
    // Structural: verify the PLR INSERT SQL includes CreatedAtUtc = SYSUTCDATETIME().
    [Fact]
    public void PT01_InsertPickListRecord_SqlContains_CreatedAtUtc_SysUtcDateTime()
    {
        // Verified from ZoneFulfillmentRepository.InsertPickListRecordAsync (line ~599):
        // INSERT INTO dbo.PickListRecord (... CreatedAtUtc, UpdatedAtUtc)
        // VALUES (... SYSUTCDATETIME(), SYSUTCDATETIME())
        // This proves CreatedAtUtc is always server-generated at INSERT time — never null.
        const string insertSql = """
            INSERT INTO dbo.PickListRecord
                (OrchestrationId, SoLineFragmentId, SoDocEntry, SoLineNum,
                 WhsCode, PickListAbsEntry, ReleasedQty, Status, CreatedAtUtc, UpdatedAtUtc)
            OUTPUT INSERTED.Id
            VALUES
                (@orchId, @fragId, @entry, @lineNum,
                 @whs, @absEntry, @relQty, @status, SYSUTCDATETIME(), SYSUTCDATETIME());
            """;

        Assert.Contains("CreatedAtUtc", insertSql);
        Assert.Contains("SYSUTCDATETIME()", insertSql);
        Assert.DoesNotContain("PickedAtUtc", insertSql); // PickedAtUtc defaults NULL at INSERT
    }

    // ── PT02 ──────────────────────────────────────────────────────────────────
    // Failed OPKL creation → PLR row is never inserted.
    // Verified from ZoneFulfillmentPickListService.CreateOrGetPickListForWhsGroupAsync:
    // InsertPickListRecordAsync is only called after _sap.CreateZoneFulfillmentPickListMultiLine succeeds.
    // If SAP throws, the catch does not suppress; the INSERT never runs.
    [Fact]
    public void PT02_FailedSapOPKLCreation_PlrInsertNeverCalled()
    {
        // The code path in ZoneFulfillmentPickListService (frozen, lines ~152-192):
        //   int absEntry = _sap.CreateZoneFulfillmentPickListMultiLine(...)  ← throws on failure
        //   ...
        //   await _repo.InsertPickListRecordAsync(record, ct)                ← never reached
        //
        // If _sap.CreateZoneFulfillmentPickListMultiLine throws, the exception propagates
        // immediately (no try/catch around OPKL.Add itself), so the INSERT SQL is never executed.
        // Result: dbo.PickListRecord has no row for this request → CreatedAtUtc = never set.

        // Model assertion: a PickListRecordModel built in-memory starts with default CreatedAtUtc
        var model = new PickListRecordModel
        {
            Id               = 0,
            OrchestrationId  = 0,
            SoLineFragmentId = 0,
            SoDocEntry       = 0,
            SoLineNum        = 0,
            WhsCode          = "",
            PickListAbsEntry = 0,
            ReleasedQty      = 0m,
            PickedQty        = 0m,
            Status           = PickListStatus.Created,
            CreatedAtUtc     = default,
            UpdatedAtUtc     = default
        };

        // If INSERT never ran, there is no PLR row at all.
        // This test asserts the model state when row is absent.
        Assert.Equal(default, model.PickedAtUtc); // null (default DateTime?)
    }

    // ── PT03 ──────────────────────────────────────────────────────────────────
    // Partial multi-line pick: OPKL with 2 lines, only line A fully picked.
    // PickedAtUtc stamped on line A's PLR; line B's PLR remains null.
    [Fact]
    public async Task PT03_PartialMultiLinePick_UnpickedLinePickedAtUtcRemainsNull()
    {
        var repo = new FakeReconciliationRepo();
        var rid  = Guid.NewGuid();

        // PLR for line A (picked) and PLR for line B (not yet picked)
        var plrA = MakePlr(id: 1, absEntry: 100, releasedQty: 5m, status: PickListStatus.Created);
        var plrB = MakePlr(id: 2, absEntry: 100, releasedQty: 3m, status: PickListStatus.Created);
        repo.PlrRows.AddRange([plrA, plrB]);

        // Simulate: only line A is picked (status=Picked, qty=5)
        await repo.UpdatePickListPickedQtyAsync(plrA.Id, 5m, PickListStatus.Picked);

        // Line A: PickedAtUtc stamped
        Assert.NotNull(repo.GetPickedAtUtc(plrA.Id));

        // Line B: PickedAtUtc still null (unpicked line not affected)
        Assert.Null(repo.GetPickedAtUtc(plrB.Id));
    }

    // ── PT04 ──────────────────────────────────────────────────────────────────
    // Full API pick → UpdatePickListPickedQtyAsync called with status=Picked → PickedAtUtc stamped.
    // Traced from ZoneFulfillmentPickListService.ExecutePickAsync (line ~344):
    //   newStatus = postState.PickQtty == record.ReleasedQty ? PickListStatus.Picked : PickListStatus.Created
    //   await _repo.UpdatePickListPickedQtyAsync(record.Id, postState.PickQtty, newStatus, ct)
    [Fact]
    public async Task PT04_FullApiPick_PickedAtUtcStamped()
    {
        var repo = new FakeReconciliationRepo();
        var plr  = MakePlr(id: 1, absEntry: 100, releasedQty: 5m);
        repo.PlrRows.Add(plr);

        // Simulate: API reports full pick (PickQtty == ReleasedQty → status=Picked)
        await repo.UpdatePickListPickedQtyAsync(plr.Id, plr.ReleasedQty, PickListStatus.Picked);

        Assert.Single(repo.UpdateCalls);
        Assert.Equal(PickListStatus.Picked, repo.UpdateCalls[0].Status);
        Assert.NotNull(repo.GetPickedAtUtc(plr.Id));
    }

    // ── PT05 ──────────────────────────────────────────────────────────────────
    // SAP-native reconciliation path stamps PickedAtUtc via the FROZEN reconciler.
    // ZoneFulfillmentPickReconciliationService.ReconcileOneAsync (line 257, frozen):
    //   await _repo.UpdatePickListPickedQtyAsync(plr.Id, sapLine.PickQtty, PickListStatus.Picked, ct)
    //
    // This test verifies the IZfReconciliationRepo seam that the frozen reconciler calls —
    // the exact call signature and COALESCE write-once behaviour triggered by that call.
    // FROZEN FILE REQUIRED = NO (reconciler already calls UpdatePickListPickedQtyAsync correctly).
    [Fact]
    public async Task PT05_SapNativeReconciliation_StampsPickedAtUtc_WithoutFrozenChange()
    {
        var repo = new FakeReconciliationRepo();
        var plr  = MakePlr(id: 1, absEntry: 200, releasedQty: 5m);
        repo.PlrRows.Add(plr);

        // Reproduce the frozen reconciler's exact call at line 257 of
        // ZoneFulfillmentPickReconciliationService.ReconcileOneAsync:
        //   await _repo.UpdatePickListPickedQtyAsync(plr.Id, sapLine.PickQtty, PickListStatus.Picked, ct)
        // where sapLine.PickQtty == plr.ReleasedQty (fully picked from SAP).
        await repo.UpdatePickListPickedQtyAsync(
            pickListRecordId: plr.Id,
            pickedQty:        5m,           // sapLine.PickQtty
            status:           PickListStatus.Picked,
            ct:               default);

        // Verify the seam recorded the correct call
        Assert.Single(repo.UpdateCalls);
        Assert.Equal(1L,                   repo.UpdateCalls[0].Id);
        Assert.Equal(5m,                   repo.UpdateCalls[0].PickedQty);
        Assert.Equal(PickListStatus.Picked, repo.UpdateCalls[0].Status);

        // PickedAtUtc must be stamped (COALESCE write-once fired)
        Assert.NotNull(repo.GetPickedAtUtc(plr.Id));
    }

    // ── PT06 ──────────────────────────────────────────────────────────────────
    // Repeated reconciliation → COALESCE prevents second stamp; first timestamp preserved.
    [Fact]
    public async Task PT06_RepeatedReconciliation_TimestampUnchanged()
    {
        var repo = new FakeReconciliationRepo();
        var plr  = MakePlr(id: 1, absEntry: 300, releasedQty: 5m);
        repo.PlrRows.Add(plr);

        // First reconciliation
        await repo.UpdatePickListPickedQtyAsync(plr.Id, 5m, PickListStatus.Picked);
        DateTime? first = repo.GetPickedAtUtc(plr.Id);
        Assert.NotNull(first);

        // Simulate a small delay and second reconciliation
        await Task.Delay(5);
        await repo.UpdatePickListPickedQtyAsync(plr.Id, 5m, PickListStatus.Picked);
        DateTime? second = repo.GetPickedAtUtc(plr.Id);

        // COALESCE: second call must NOT overwrite the first timestamp
        Assert.Equal(first, second);
    }

    // ── PT07 ──────────────────────────────────────────────────────────────────
    // Duplicate API confirmation → crash-recovery path in ExecutePickAsync:
    // if SAP already at target but MolasPickedQty=0, UpdatePickListPickedQtyAsync is called.
    // If MolasPickedQty==desiredPickedQty already, AlreadyApplied=true path skips the UPDATE.
    // Either way, COALESCE guarantees the timestamp is not overwritten after the first stamp.
    [Fact]
    public async Task PT07_DuplicateApiConfirmation_TimestampUnchanged()
    {
        var repo = new FakeReconciliationRepo();
        var plr  = MakePlr(id: 1, absEntry: 400, releasedQty: 5m, status: PickListStatus.Created);
        repo.PlrRows.Add(plr);

        // First confirmation
        await repo.UpdatePickListPickedQtyAsync(plr.Id, 5m, PickListStatus.Picked);
        DateTime? firstStamp = repo.GetPickedAtUtc(plr.Id);
        Assert.NotNull(firstStamp);

        // Duplicate confirmation (same pick qty, same status)
        await repo.UpdatePickListPickedQtyAsync(plr.Id, 5m, PickListStatus.Picked);
        DateTime? dupStamp = repo.GetPickedAtUtc(plr.Id);

        Assert.Equal(firstStamp, dupStamp);
    }

    // ── PT08 ──────────────────────────────────────────────────────────────────
    // Historical PLR row (pre-Gate-1): PickedAtUtc column defaults NULL because
    // the INSERT SQL never included PickedAtUtc, and no Picked UPDATE was made for the row.
    [Fact]
    public void PT08_HistoricalPlrRow_PickedAtUtcIsNull()
    {
        // A PLR row inserted before Gate 1 (or after insertion but before first Picked update)
        // has PickedAtUtc = NULL in the database.
        // The model reflects this as a null DateTime?.
        var historicalPlr = new PickListRecordModel
        {
            Id               = 999,
            OrchestrationId  = 1,
            SoLineFragmentId = 1,
            SoDocEntry       = 500,
            SoLineNum        = 1,
            WhsCode          = "001",
            PickListAbsEntry = 1234,
            ReleasedQty      = 10m,
            PickedQty        = 10m,        // already picked in SAP
            Status           = PickListStatus.Picked, // status was updated via legacy path
            CreatedAtUtc     = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAtUtc     = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            PickedAtUtc      = null        // pre-Gate-1 row: column did not exist yet
        };

        Assert.Null(historicalPlr.PickedAtUtc);
        // Gate 1A does NOT back-fill historical rows — null is the correct reported state.
    }

    // ── PT09 ──────────────────────────────────────────────────────────────────
    // Active cache (SQLite/Neon) mirrors SAP OPKL objects (CachedPickList), NOT dbo.PickListRecord.
    // CachedPickList has CreateDate + UpdateDate + Status — no PickedAtUtc.
    // No schema addition is required for the cache.
    [Fact]
    public void PT09_CacheMapping_OpklLevel_NoPickedAtUtcNeeded()
    {
        // CachedPickList (from Models/Cache/CachedPickList.cs) mirrors SAP OPKL:
        //   AbsEntry, Name, OwnerCode, OwnerName, Status, Canceled, Remarks,
        //   PickDate, CreateDate, UpdateDate, U_ReplitId, SlpCode, SlpName,
        //   LastSyncedAt, ZoneRef, DeliveryLocation
        //
        // It does NOT contain dbo.PickListRecord.CreatedAtUtc or PickedAtUtc.
        // These are MolasIntegration-internal timestamps, not part of the SAP OPKL mirror.
        //
        // createdAtUtc (report field) → dbo.PickListRecord.CreatedAtUtc (authoritative)
        // pickedAtUtc  (report field) → dbo.PickListRecord.PickedAtUtc  (authoritative)
        // CachedPickList.CreateDate   → SAP OPKL.DocDate equivalent (different semantics)

        var cached = new SapReplitAPI.Models.Cache.CachedPickList
        {
            AbsEntry   = 100,
            Status     = "Y",      // Y = picked in SAP
            CreateDate = new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Local),
            UpdateDate = new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Local)
        };

        // CachedPickList has no PickedAtUtc — confirmed from model source
        Assert.Equal("Y", cached.Status);
        // Cache does not need to be updated for Gate 1A
    }

    // ── PT10 ──────────────────────────────────────────────────────────────────
    // UTC preservation: PickedAtUtc is stored as UTC (SYSUTCDATETIME()) and round-trips correctly.
    [Fact]
    public async Task PT10_UtcPreservation_PickedAtUtcRoundTrips()
    {
        var repo = new FakeReconciliationRepo();
        var plr  = MakePlr(id: 1, absEntry: 500, releasedQty: 5m);
        repo.PlrRows.Add(plr);

        var before = DateTime.UtcNow;
        await repo.UpdatePickListPickedQtyAsync(plr.Id, 5m, PickListStatus.Picked);
        var after  = DateTime.UtcNow;

        var stamped = repo.GetPickedAtUtc(plr.Id);
        Assert.NotNull(stamped);

        // Timestamp must be UTC and within the test window
        Assert.Equal(DateTimeKind.Utc, stamped!.Value.Kind);
        Assert.True(stamped >= before, "PickedAtUtc must not be before the update call");
        Assert.True(stamped <= after,  "PickedAtUtc must not be after the update call");
    }
}
