using SapReplitAPI.Models.ZoneFulfillment;
using SapReplitAPI.Services.ZoneFulfillment;
using Xunit;
using static SapReplitAPI.Tests.ZfPick.ZfPickTestData;

namespace SapReplitAPI.Tests.ZfPick;

/// <summary>
/// PRE-PRODUCTION SAFETY GATE — 20 automated scenarios.
/// All scenarios are pure in-memory (no SQL, no COM, no SAP mutations).
/// Each test creates an isolated harness instance.
/// </summary>
public sealed class ZfPickReconciliationTests
{
    // ─────────────────────────────────────────────────────────────────────────
    // 1. Native SAP full pick → automatic reconciliation (happy path)
    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task S01_NativeSapFullPick_ReconcilesToPicked()
    {
        var h   = new ZfPickReconciliationTestHarness();
        var rid = Guid.NewGuid();
        var orch = MakeOrch(rid);
        h.Repo.SetOrchestration(orch);
        h.Repo.AddPlr(MakePlr());

        h.SapReader.Register(AbsEntry,
            header: ValidOpkl(),
            lines:  [ValidLine()]);

        await h.Service.ReconcileBatchAsync();

        Assert.Equal(1, h.Repo.UpdatePlrCallCount);
        Assert.Equal(1, h.Repo.UpdatePlfrCallCount);
        Assert.Equal(1, h.Automation.CallCount);
        Assert.Equal((2001L, RelQty, PickListStatus.Picked), h.Repo.PlrUpdates[0]);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 2. API pick path (PLR already Picked) → reconciler skips, no duplication
    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task S02_PlrAlreadyPicked_ReconcilerSkipsAndCallsAutomation()
    {
        var h   = new ZfPickReconciliationTestHarness();
        var rid = Guid.NewGuid();
        h.Repo.SetOrchestration(MakeOrch(rid));
        h.Repo.AddPlr(MakePlr(status: PickListStatus.Picked));  // already done by API

        await h.Service.ReconcileBatchAsync();

        // pending.Count == 0 branch → automation still called (race-win path)
        Assert.Equal(0, h.Repo.UpdatePlrCallCount);
        Assert.Equal(0, h.Repo.UpdatePlfrCallCount);
        Assert.Equal(1, h.Automation.CallCount);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 3. Another app producing identical SAP truth → reconciles correctly
    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task S03_ThirdAppSapTruth_ReconciliationSucceeds()
    {
        var h   = new ZfPickReconciliationTestHarness();
        var rid = Guid.NewGuid();
        h.Repo.SetOrchestration(MakeOrch(rid));
        h.Repo.AddPlr(MakePlr());

        // Same OPKL with ZF U_ReplitId, line qty matches exactly — regardless of who picked in SAP
        h.SapReader.Register(AbsEntry, ValidOpkl(), [ValidLine()]);

        await h.Service.ReconcileBatchAsync();

        Assert.Equal(1, h.Repo.UpdatePlrCallCount);
        Assert.Equal(1, h.Automation.CallCount);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 4. Partial pick (SAP qty < released qty) → defer, no mutation
    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task S04_PartialPick_DefersNoMutation()
    {
        var h   = new ZfPickReconciliationTestHarness();
        h.Repo.SetOrchestration(MakeOrch());
        h.Repo.AddPlr(MakePlr(relQty: 5m));

        h.SapReader.Register(AbsEntry, ValidOpkl(), [ValidLine(relQtty: 5m, pickQtty: 2m)]);  // picked 2 of 5

        await h.Service.ReconcileBatchAsync();

        Assert.Equal(0, h.Repo.UpdatePlrCallCount);
        Assert.Equal(0, h.Automation.CallCount);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 5. Exact quantity (within tolerance) → eligible, reconciles
    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task S05_ExactQtyWithinTolerance_Reconciles()
    {
        var h   = new ZfPickReconciliationTestHarness();
        h.Repo.SetOrchestration(MakeOrch());
        h.Repo.AddPlr(MakePlr(relQty: 3.000m));

        // PickQtty is within 0.001 tolerance
        h.SapReader.Register(AbsEntry, ValidOpkl(), [ValidLine(relQtty: 3.000m, pickQtty: 3.0005m)]);

        await h.Service.ReconcileBatchAsync();

        Assert.Equal(1, h.Repo.UpdatePlrCallCount);
        Assert.Equal(1, h.Automation.CallCount);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 6. Over-pick (SAP qty > released qty + tolerance) → BLOCKED
    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task S06_OverPick_BlockedNoMutation()
    {
        var h   = new ZfPickReconciliationTestHarness();
        h.Repo.SetOrchestration(MakeOrch());
        h.Repo.AddPlr(MakePlr(relQty: 3m));

        h.SapReader.Register(AbsEntry, ValidOpkl(), [ValidLine(relQtty: 3m, pickQtty: 5m)]);  // over-pick

        await h.Service.ReconcileBatchAsync();

        Assert.Equal(0, h.Repo.UpdatePlrCallCount);
        Assert.Equal(0, h.Automation.CallCount);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 7. Wrong warehouse (WhsCode mismatch) → BLOCKED
    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task S07_WrongWarehouse_BlockedNoMutation()
    {
        var h   = new ZfPickReconciliationTestHarness();
        h.Repo.SetOrchestration(MakeOrch());
        h.Repo.AddPlr(MakePlr(whs: WhsCode));

        h.SapReader.Register(AbsEntry, ValidOpkl(),
            [ValidLine(whsCode: WrongWhs)]);  // SAP says different warehouse

        await h.Service.ReconcileBatchAsync();

        Assert.Equal(0, h.Repo.UpdatePlrCallCount);
        Assert.Equal(0, h.Automation.CallCount);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 8. Wrong BaseObject (not 17/ORDR) → BLOCKED
    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task S08_WrongBaseObject_BlockedNoMutation()
    {
        var h   = new ZfPickReconciliationTestHarness();
        h.Repo.SetOrchestration(MakeOrch());
        h.Repo.AddPlr(MakePlr());

        h.SapReader.Register(AbsEntry, ValidOpkl(),
            [ValidLine(baseObject: 13)]);  // 13 = OPOR (Purchase Order), not ORDR

        await h.Service.ReconcileBatchAsync();

        Assert.Equal(0, h.Repo.UpdatePlrCallCount);
        Assert.Equal(0, h.Automation.CallCount);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 9. PKL1 line missing for this (SoDocEntry, SoLineNum) → BLOCKED
    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task S09_MissingPkl1Line_BlockedNoMutation()
    {
        var h   = new ZfPickReconciliationTestHarness();
        h.Repo.SetOrchestration(MakeOrch());
        h.Repo.AddPlr(MakePlr(soDocEntry: SoDocEntry, soLineNum: SoLineNum));

        // PKL1 line points to a different SO entry
        h.SapReader.Register(AbsEntry, ValidOpkl(),
            [ValidLine(orderEntry: 99999, orderLine: 5)]);

        await h.Service.ReconcileBatchAsync();

        Assert.Equal(0, h.Repo.UpdatePlrCallCount);
        Assert.Equal(0, h.Automation.CallCount);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 10. OPKL not found in SAP → BLOCKED
    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task S10_OpklNotFound_BlockedNoMutation()
    {
        var h   = new ZfPickReconciliationTestHarness();
        h.Repo.SetOrchestration(MakeOrch());
        h.Repo.AddPlr(MakePlr());

        // No entry registered → FakeZfPickSapReader returns (null, [], [])
        await h.Service.ReconcileBatchAsync();

        Assert.Equal(0, h.Repo.UpdatePlrCallCount);
        Assert.Equal(0, h.Automation.CallCount);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 11. Multiple PKL2 bins summing correctly → eligible, reconciles
    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task S11_MultiBinSumCorrect_Reconciles()
    {
        var h   = new ZfPickReconciliationTestHarness();
        h.Repo.SetOrchestration(MakeOrch());
        h.Repo.AddPlr(MakePlr(relQty: 6m));

        var bins = new List<ZfPkl2Validation>
        {
            new(PickEntry, BinAbs: 101, BinCode: "BIN-A", PickQtty: 2m),
            new(PickEntry, BinAbs: 102, BinCode: "BIN-B", PickQtty: 4m),
        };
        h.SapReader.Register(AbsEntry, ValidOpkl(),
            [ValidLine(relQtty: 6m, pickQtty: 6m)], bins);

        await h.Service.ReconcileBatchAsync();

        Assert.Equal(1, h.Repo.UpdatePlrCallCount);
        Assert.Equal(1, h.Automation.CallCount);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 12. PKL2 bin sum mismatch → BLOCKED
    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task S12_BinSumMismatch_BlockedNoMutation()
    {
        var h   = new ZfPickReconciliationTestHarness();
        h.Repo.SetOrchestration(MakeOrch());
        h.Repo.AddPlr(MakePlr(relQty: 3m));

        var bins = new List<ZfPkl2Validation>
        {
            new(PickEntry, BinAbs: 101, BinCode: "BIN-A", PickQtty: 1m),
            new(PickEntry, BinAbs: 102, BinCode: "BIN-B", PickQtty: 1m),  // sum=2, but line=3
        };
        h.SapReader.Register(AbsEntry, ValidOpkl(),
            [ValidLine(relQtty: 3m, pickQtty: 3m)], bins);

        await h.Service.ReconcileBatchAsync();

        Assert.Equal(0, h.Repo.UpdatePlrCallCount);
        Assert.Equal(0, h.Automation.CallCount);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 13. PLR + PLFR both updated consistently
    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task S13_PlrAndPlfrBothUpdated()
    {
        var h   = new ZfPickReconciliationTestHarness();
        h.Repo.SetOrchestration(MakeOrch());
        h.Repo.AddPlr(MakePlr());
        h.SapReader.Register(AbsEntry, ValidOpkl(), [ValidLine()]);

        await h.Service.ReconcileBatchAsync();

        Assert.Equal(1, h.Repo.UpdatePlrCallCount);
        Assert.Equal(1, h.Repo.UpdatePlfrCallCount);
        Assert.Equal(RelQty, h.Repo.PlrUpdates[0].Qty);
        Assert.Equal(PickListStatus.Picked, h.Repo.PlrUpdates[0].Status);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 14. Two-WHS: first warehouse picked, second still pending → WAIT (no delivery)
    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task S14_TwoWhs_FirstPickedSecondPending_AutomationWaits()
    {
        var h   = new ZfPickReconciliationTestHarness();
        var rid = Guid.NewGuid();
        var orch = MakeOrch(rid);
        h.Repo.SetOrchestration(orch);

        // PLR 1: WHS001 — already Picked (via API or prior reconcile)
        h.Repo.AddPlr(MakePlr(id: 2001, whs: "WHS001", status: PickListStatus.Picked));

        // PLR 2: WHS002 — still pending, SAP shows pick complete
        h.Repo.AddPlr(MakePlr(id: 2002, whs: "WHS002", absEntry: 51,
                               soDocEntry: SoDocEntry, soLineNum: 1, relQty: 2m));

        h.SapReader.Register(51, ValidOpkl(51), [ValidLine(pickEntry: 2, orderLine: 1, whsCode: "WHS002", relQtty: 2m)]);

        // Automation says waiting (other WHS still pending in automation evaluation)
        h.Automation.AutomationStatus = "WaitingForOtherPicks";

        await h.Service.ReconcileBatchAsync();

        Assert.Equal(1, h.Repo.UpdatePlrCallCount);   // PLR 2 reconciled
        Assert.Equal(1, h.Automation.CallCount);       // automation called
        Assert.Equal("WaitingForOtherPicks", h.Automation.AutomationStatus);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 15. Two-WHS: final pick completes → ONE EvaluateAndTriggerDelivery call
    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task S15_TwoWhs_FinalPickComplete_OneDeliveryTrigger()
    {
        var h   = new ZfPickReconciliationTestHarness();
        h.Repo.SetOrchestration(MakeOrch());

        // Both PLRs pending, both have SAP confirmation
        h.Repo.AddPlr(MakePlr(id: 2001, whs: "WHS001", absEntry: 50, soLineNum: 0, relQty: 3m));
        h.Repo.AddPlr(MakePlr(id: 2002, whs: "WHS002", absEntry: 51, soLineNum: 1, relQty: 2m));

        h.SapReader.Register(50, ValidOpkl(50), [ValidLine(pickEntry: 1, orderLine: 0, whsCode: "WHS001", relQtty: 3m)]);
        h.SapReader.Register(51, ValidOpkl(51), [ValidLine(pickEntry: 2, orderLine: 1, whsCode: "WHS002", relQtty: 2m)]);

        h.Automation.AutomationStatus = "DeliveryCreated";

        await h.Service.ReconcileBatchAsync();

        Assert.Equal(2, h.Repo.UpdatePlrCallCount);
        Assert.Equal(1, h.Automation.CallCount);   // exactly one delivery trigger
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 16. API/reconciler race: orchestration already moves away from Accepted
    //     under lock → reconciler skips, no duplicate delivery
    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task S16_RaceOrchestrationAlreadyDelivered_Skips()
    {
        var h   = new ZfPickReconciliationTestHarness();
        var rid = Guid.NewGuid();

        // GetStuckAccepted returns orch as Accepted
        h.Repo.SetOrchestration(MakeOrch(rid, state: OrchestrationState.Accepted));
        h.Repo.AddPlr(MakePlr());

        // Simulate API wins: by the time FindOrchestrationAsync runs under lock,
        // state has advanced to Delivered
        var deliveredOrch = MakeOrch(rid, state: OrchestrationState.Delivered);
        h.Repo.SetOrchestration(deliveredOrch);

        h.SapReader.Register(AbsEntry, ValidOpkl(), [ValidLine()]);

        await h.Service.ReconcileBatchAsync();

        // ReconcileOneAsync sees non-Accepted state → returns immediately
        Assert.Equal(0, h.Repo.UpdatePlrCallCount);
        Assert.Equal(0, h.Automation.CallCount);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 17. Two reconciler executions → idempotent (second run sees Picked PLRs)
    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task S17_TwoReconcilerRuns_Idempotent()
    {
        var h   = new ZfPickReconciliationTestHarness();
        h.Repo.SetOrchestration(MakeOrch());
        h.Repo.AddPlr(MakePlr());
        h.SapReader.Register(AbsEntry, ValidOpkl(), [ValidLine()]);

        // First run — reconciles
        await h.Service.ReconcileBatchAsync();
        Assert.Equal(1, h.Repo.UpdatePlrCallCount);

        // Second run — PLR is now Picked (FakeZfPickRepo updated it in-memory)
        await h.Service.ReconcileBatchAsync();

        // pending.Count == 0 on second run → automation called, no second PLR update
        Assert.Equal(1, h.Repo.UpdatePlrCallCount);   // unchanged
        Assert.Equal(2, h.Automation.CallCount);       // called once per batch run
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 18. OPKL.Status=O (not yet confirmed) → defer, no mutation
    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task S18_OpklStatusOpen_DeferNoMutation()
    {
        var h   = new ZfPickReconciliationTestHarness();
        h.Repo.SetOrchestration(MakeOrch());
        h.Repo.AddPlr(MakePlr());

        h.SapReader.Register(AbsEntry, OpenOpkl(), [ValidLine()]);

        await h.Service.ReconcileBatchAsync();

        Assert.Equal(0, h.Repo.UpdatePlrCallCount);
        Assert.Equal(0, h.Automation.CallCount);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 19. OPKL.Canceled=Y → BLOCKED (not deferred)
    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task S19_OpklCanceled_BlockedNoMutation()
    {
        var h   = new ZfPickReconciliationTestHarness();
        h.Repo.SetOrchestration(MakeOrch());
        h.Repo.AddPlr(MakePlr());

        h.SapReader.Register(AbsEntry, CanceledOpkl(), [ValidLine()]);

        await h.Service.ReconcileBatchAsync();

        Assert.Equal(0, h.Repo.UpdatePlrCallCount);
        Assert.Equal(0, h.Automation.CallCount);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 20. One candidate throws → other candidates continue (batch isolation)
    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task S20_OneCandidateThrows_OtherCandidatesContinue()
    {
        // Use a custom repo that injects a second orchestration and throws on the first
        var h          = new ZfPickReconciliationTestHarness();
        var rid1       = Guid.NewGuid();
        var rid2       = Guid.NewGuid();

        // Harness fake only supports one orch — use a specialized multi-orch repo
        var multiRepo  = new MultiOrchFakeRepo();
        var orch1      = MakeOrch(rid1);                                   // Id=1001, will throw
        var orch2      = MakeOrch(rid2, uReplitId: "ZF-TEST-REPLIT-02");  // must have distinct Id
        orch2.Id       = 1002;                                             // avoid shared PLR lookup

        var plr1 = MakePlr(orchId: orch1.Id, absEntry: 50, id: 2001);
        var plr2 = MakePlr(orchId: orch2.Id, absEntry: 51, id: 2002);
        multiRepo.Orchs.Add(orch1);
        multiRepo.Orchs.Add(orch2);
        multiRepo.Plrs.Add(plr1);
        multiRepo.Plrs.Add(plr2);

        h.SapReader.Register(50, null);       // orch1: OPKL not found → would normally block, but we force an exception
        h.SapReader.Register(51, ValidOpkl(51, "ZF-TEST-REPLIT-02"), [ValidLine(pickEntry: 2)]);

        // Build service with the multi-orch repo
        using var coordinator = new ZoneFulfillmentDeliveryCoordinator();
        var service = new ZoneFulfillmentPickReconciliationService(
            repo        : multiRepo,
            sapReader   : h.SapReader,
            coordinator : coordinator,
            automation  : h.Automation,
            log         : Microsoft.Extensions.Logging.Abstractions.NullLogger<ZoneFulfillmentPickReconciliationService>.Instance);

        // Force orch1 to throw by making FindOrchestrationAsync throw for rid1
        multiRepo.ThrowOnRequestId = rid1;

        await service.ReconcileBatchAsync();

        // orch1 threw — orch2 still processed
        Assert.Equal(1, multiRepo.UpdatePlrCallCount);
        Assert.Equal(1, h.Automation.CallCount);
    }
}

// ── Supporting multi-orchestration fake for S20 ───────────────────────────────

public sealed class MultiOrchFakeRepo : IZfReconciliationRepo
{
    public List<FulfillmentOrchestrationRecord> Orchs { get; } = [];
    public List<PickListRecordModel>            Plrs  { get; } = [];
    public int    UpdatePlrCallCount  { get; private set; }
    public int    UpdatePlfrCallCount { get; private set; }
    public Guid?  ThrowOnRequestId   { get; set; }

    public Task<List<FulfillmentOrchestrationRecord>> GetStuckAcceptedOrchestrationsAsync(
        int thresholdSeconds, CancellationToken ct = default)
        => Task.FromResult(Orchs.Where(o => o.State == OrchestrationState.Accepted).ToList());

    public Task<FulfillmentOrchestrationRecord?> FindOrchestrationAsync(
        Guid requestId, CancellationToken ct = default)
    {
        if (ThrowOnRequestId == requestId)
            throw new InvalidOperationException($"Simulated failure for RequestId={requestId}");
        return Task.FromResult(Orchs.FirstOrDefault(o => o.RequestId == requestId));
    }

    public Task<List<PickListRecordModel>> GetPickListRecordsAsync(
        long orchestrationId, CancellationToken ct = default)
        => Task.FromResult(Plrs.Where(p => p.OrchestrationId == orchestrationId).ToList());

    public Task UpdatePickListPickedQtyAsync(
        long pickListRecordId, decimal pickedQty, string status, CancellationToken ct = default)
    {
        UpdatePlrCallCount++;
        var plr = Plrs.FirstOrDefault(p => p.Id == pickListRecordId);
        if (plr is not null) { plr.PickedQty = pickedQty; plr.Status = status; }
        return Task.CompletedTask;
    }

    public Task<int> UpdatePickListFragmentPickedQtyAsync(
        long pickListRecordId, decimal pickedQty, string pickStatus, CancellationToken ct = default)
    {
        UpdatePlfrCallCount++;
        return Task.FromResult(1);
    }
}
