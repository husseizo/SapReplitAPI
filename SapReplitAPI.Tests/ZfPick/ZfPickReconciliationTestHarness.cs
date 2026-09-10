using Microsoft.Extensions.Logging.Abstractions;
using SapReplitAPI.Models.ZoneFulfillment;
using SapReplitAPI.Services.ZoneFulfillment;

namespace SapReplitAPI.Tests.ZfPick;

/// <summary>
/// Test harness for ZoneFulfillmentPickReconciliationService.
/// All external dependencies replaced with in-memory fakes via extracted interfaces.
/// </summary>
public sealed class ZfPickReconciliationTestHarness
{
    public FakeZfPickSapReader            SapReader   { get; } = new();
    public FakeZfPickRepo                 Repo        { get; } = new();
    public FakeZfAutomation               Automation  { get; } = new();
    public ZoneFulfillmentDeliveryCoordinator Coordinator { get; } = new();

    public ZoneFulfillmentPickReconciliationService Service { get; }

    public ZfPickReconciliationTestHarness()
    {
        Service = new ZoneFulfillmentPickReconciliationService(
            repo        : Repo,
            sapReader   : SapReader,
            coordinator : Coordinator,
            automation  : Automation,
            log         : NullLogger<ZoneFulfillmentPickReconciliationService>.Instance);
    }
}

/// <summary>In-memory fake for IZfReconciliationRepo.</summary>
public sealed class FakeZfPickRepo : IZfReconciliationRepo
{
    private FulfillmentOrchestrationRecord? _orch;
    private readonly List<PickListRecordModel> _plrs = [];

    public int UpdatePlrCallCount  { get; private set; }
    public int UpdatePlfrCallCount { get; private set; }
    public List<(long Id, decimal Qty, string Status)> PlrUpdates { get; } = [];

    public void SetOrchestration(FulfillmentOrchestrationRecord orch) => _orch = orch;
    public void AddPlr(PickListRecordModel plr) => _plrs.Add(plr);

    /// <summary>Set to true to simulate a DeliveryRecord existing (excludes from Case 2 recovery).</summary>
    public bool DeliveryExists { get; set; }

    public Task<List<FulfillmentOrchestrationRecord>> GetStuckAcceptedOrchestrationsAsync(
        int thresholdSeconds, CancellationToken ct = default)
    {
        if (_orch is not { State: OrchestrationState.Accepted })
            return Task.FromResult(new List<FulfillmentOrchestrationRecord>());

        bool hasNonPicked = _plrs.Any(p => p.Status != PickListStatus.Picked && p.PickListAbsEntry > 0);
        bool allPicked    = _plrs.Count > 0 && _plrs.All(p => p.Status == PickListStatus.Picked);

        // Case 1: at least one non-Picked PLR (SAP-native confirm may be in flight)
        // Case 2: all PLRs Picked but no DeliveryRecord yet (deadlock-recovery path)
        bool eligible = hasNonPicked || (allPicked && !DeliveryExists);

        return Task.FromResult(eligible
            ? new List<FulfillmentOrchestrationRecord> { _orch }
            : new List<FulfillmentOrchestrationRecord>());
    }

    public Task<FulfillmentOrchestrationRecord?> FindOrchestrationAsync(
        Guid requestId, CancellationToken ct = default)
        => Task.FromResult(_orch?.RequestId == requestId ? _orch : null);

    public Task<List<PickListRecordModel>> GetPickListRecordsAsync(
        long orchestrationId, CancellationToken ct = default)
        => Task.FromResult(_plrs.Where(p => p.OrchestrationId == orchestrationId).ToList());

    public Task UpdatePickListPickedQtyAsync(
        long pickListRecordId, decimal pickedQty, string status, CancellationToken ct = default)
    {
        UpdatePlrCallCount++;
        PlrUpdates.Add((pickListRecordId, pickedQty, status));
        var plr = _plrs.FirstOrDefault(p => p.Id == pickListRecordId);
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

/// <summary>In-memory fake for IZfAutomation.</summary>
public sealed class FakeZfAutomation : IZfAutomation
{
    public int     CallCount       { get; private set; }
    public string  AutomationStatus { get; set; } = "DeliveryCreated";
    public int?    DeliveryDocEntry { get; set; } = 99001;
    public int?    InvoiceDocEntry  { get; set; } = 99002;

    public Task<PostPickAutomationResult> EvaluateAndTriggerDeliveryAsync(
        Guid requestId, CancellationToken ct = default)
    {
        CallCount++;
        bool waiting = AutomationStatus == "WaitingForOtherPicks";
        return Task.FromResult(new PostPickAutomationResult
        {
            AutomationStatus         = AutomationStatus,
            AllRequiredPicksComplete = !waiting,
            DeliveryTriggered        = !waiting,
            DeliveryDocEntry         = waiting ? null : DeliveryDocEntry,
            InvoiceDocEntry          = waiting ? null : InvoiceDocEntry,
        });
    }
}

/// <summary>
/// Automation fake that verifies the coordinator lock is NOT held when it is called.
/// Attempts to acquire the per-RequestId semaphore; if the reconciler released it first,
/// acquisition succeeds within the 2-second timeout.
/// </summary>
public sealed class LockCheckFakeAutomation : IZfAutomation
{
    private readonly ZoneFulfillmentDeliveryCoordinator _coordinator;

    public int  CallCount              { get; private set; }
    public bool LockAcquiredSuccessfully { get; private set; }

    public LockCheckFakeAutomation(ZoneFulfillmentDeliveryCoordinator coordinator)
        => _coordinator = coordinator;

    public async Task<PostPickAutomationResult> EvaluateAndTriggerDeliveryAsync(
        Guid requestId, CancellationToken ct = default)
    {
        CallCount++;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            // Will deadlock (timeout) if the reconciler still holds the lock.
            using var lk = await _coordinator.AcquireAsync(requestId, cts.Token);
            LockAcquiredSuccessfully = true;
        }
        catch (OperationCanceledException)
        {
            LockAcquiredSuccessfully = false;
        }
        return new PostPickAutomationResult
        {
            AutomationStatus         = "DeliveryCreated",
            AllRequiredPicksComplete = true,
            DeliveryTriggered        = true,
            DeliveryDocEntry         = 99001,
            InvoiceDocEntry          = 99002,
        };
    }
}

/// <summary>Shared test data builders.</summary>
public static class ZfPickTestData
{
    public const string UReplitId    = "ZF-TEST-REPLIT-01";
    public const string WrongReplitId = "ZF-OTHER-ORCH-99";
    public const string WhsCode      = "WHS001";
    public const string WrongWhs     = "WHS999";
    public const int    SoDocEntry   = 28000;
    public const int    SoLineNum    = 0;
    public const int    AbsEntry     = 50;
    public const int    PickEntry    = 1;
    public const decimal RelQty      = 3m;

    public static FulfillmentOrchestrationRecord MakeOrch(
        Guid? rid = null, string state = OrchestrationState.Accepted,
        string? uReplitId = UReplitId)
        => new()
        {
            Id               = 1001,
            RequestId        = rid ?? Guid.NewGuid(),
            State            = state,
            U_ReplitId       = uReplitId,
            SoDocEntry       = SoDocEntry,
            SoDocNum         = 28000,
            DeliveryLocation = "TestLoc",
            AllocationVersion = 1,
            CreatedAtUtc     = DateTime.UtcNow.AddMinutes(-10),
            UpdatedAtUtc     = DateTime.UtcNow.AddMinutes(-5)
        };

    public static PickListRecordModel MakePlr(
        long orchId = 1001,
        string status = PickListStatus.Created,
        int absEntry = AbsEntry,
        decimal relQty = RelQty,
        string whs = WhsCode,
        int soDocEntry = SoDocEntry,
        int soLineNum = SoLineNum,
        long id = 2001)
        => new()
        {
            Id               = id,
            OrchestrationId  = orchId,
            SoLineFragmentId = 3001,
            SoDocEntry       = soDocEntry,
            SoLineNum        = soLineNum,
            WhsCode          = whs,
            PickListAbsEntry = absEntry,
            ReleasedQty      = relQty,
            PickedQty        = 0,
            Status           = status,
            CreatedAtUtc     = DateTime.UtcNow.AddMinutes(-10),
            UpdatedAtUtc     = DateTime.UtcNow.AddMinutes(-5)
        };

    public static ZfOpklValidation ValidOpkl(int absEntry = AbsEntry, string? uReplitId = UReplitId)
        => new(absEntry, Status: "Y", Canceled: "N", UReplitId: uReplitId);

    public static ZfOpklValidation OpenOpkl(int absEntry = AbsEntry, string? uReplitId = UReplitId)
        => new(absEntry, Status: "O", Canceled: "N", UReplitId: uReplitId);

    public static ZfOpklValidation CanceledOpkl(int absEntry = AbsEntry)
        => new(absEntry, Status: "Y", Canceled: "Y", UReplitId: UReplitId);

    public static ZfPkl1Validation ValidLine(
        int pickEntry = PickEntry, int orderEntry = SoDocEntry, int orderLine = SoLineNum,
        string whsCode = WhsCode, decimal relQtty = RelQty, decimal? pickQtty = null,
        string pickStatus = "Y", int baseObject = 17)
        => new(pickEntry, orderEntry, orderLine, baseObject, whsCode, relQtty, pickQtty ?? relQtty, pickStatus);

    public static ZfPkl2Validation MakeBin(int pickEntry = PickEntry, int binAbs = 101,
        string binCode = "BIN-A", decimal qty = 0)
        => new(pickEntry, binAbs, binCode, qty > 0 ? qty : RelQty);
}
