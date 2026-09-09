using SapReplitAPI.Models.Offline;
using SapReplitAPI.Tests.Fakes;
using Xunit;

namespace SapReplitAPI.Tests.Offline;

/// <summary>
/// ReconciliationRequired fail-closed tests.
///
/// Physical fulfillment truth is IMMUTABLE. When SAP disagrees with recorded
/// picks (wrong bin availability, bin not found, customer invalid, preflight fail),
/// recovery MUST transition to ReconciliationRequired — never silently reallocate.
///
/// FAIL-CLOSED DESIGN: On any SAP disagreement, recovery stops and human
/// intervention is required. This is the safer path over silent reallocation.
/// </summary>
public sealed class ReconciliationTests
{
    [Fact]
    public async Task Reconciliation_SapBinShortage_MarksReconciliationRequired()
    {
        using var db  = OfflineTestDb.Build();
        var adapter = new FakeOfflineSapAdapter
        {
            PickReplayConfig = FakeSapStageConfig.BinShortage()
        };
        var svc     = OfflineTestDb.BuildRecoveryService(db, adapter);
        int orderId = await OfflineTestDb.SeedWaitingForRecoveryOrder(db);

        await svc.RecoverBatchAsync();

        var order = await db.OfflineFulfillmentOrders.FindAsync(orderId);
        Assert.Equal(OfflineFulfillmentState.ReconciliationRequired, order!.State);
        Assert.Equal(ReconciliationReasonCode.SapBinShortage, order.ReconciliationReason);
    }

    [Fact]
    public async Task Reconciliation_SapBinNotFound_MarksReconciliationRequired()
    {
        using var db  = OfflineTestDb.Build();
        var adapter = new FakeOfflineSapAdapter
        {
            PickReplayConfig = FakeSapStageConfig.BinNotFound()
        };
        var svc     = OfflineTestDb.BuildRecoveryService(db, adapter);
        int orderId = await OfflineTestDb.SeedWaitingForRecoveryOrder(db);

        await svc.RecoverBatchAsync();

        var order = await db.OfflineFulfillmentOrders.FindAsync(orderId);
        Assert.Equal(OfflineFulfillmentState.ReconciliationRequired, order!.State);
        Assert.Equal(ReconciliationReasonCode.BinNotFound, order.ReconciliationReason);
    }

    [Fact]
    public async Task Reconciliation_SapOrdrFailure_MarksReconciliationRequired_With_Reason()
    {
        using var db  = OfflineTestDb.Build();
        var adapter = new FakeOfflineSapAdapter
        {
            OrderConfig = FakeSapStageConfig.Failure("Customer code invalid in SAP")
        };
        var svc     = OfflineTestDb.BuildRecoveryService(db, adapter);
        int orderId = await OfflineTestDb.SeedWaitingForRecoveryOrder(db);

        await svc.RecoverBatchAsync();

        var order = await db.OfflineFulfillmentOrders.FindAsync(orderId);
        Assert.Equal(OfflineFulfillmentState.ReconciliationRequired, order!.State);
        Assert.Equal(ReconciliationReasonCode.SapPreflightFailed, order.ReconciliationReason);
        Assert.Contains("Customer code invalid", order.ErrorMessage);
    }

    [Fact]
    public async Task Reconciliation_SapOdlnFailure_MarksReconciliationRequired()
    {
        using var db  = OfflineTestDb.Build();
        var adapter = new FakeOfflineSapAdapter
        {
            DeliveryConfig = FakeSapStageConfig.Failure("ODLN could not be created")
        };
        var svc     = OfflineTestDb.BuildRecoveryService(db, adapter);
        int orderId = await OfflineTestDb.SeedWaitingForRecoveryOrder(db);

        await svc.RecoverBatchAsync();

        var order = await db.OfflineFulfillmentOrders.FindAsync(orderId);
        Assert.Equal(OfflineFulfillmentState.ReconciliationRequired, order!.State);
    }

    [Fact]
    public async Task Reconciliation_SapOinvFailure_MarksReconciliationRequired()
    {
        using var db  = OfflineTestDb.Build();
        var adapter = new FakeOfflineSapAdapter
        {
            InvoiceConfig = FakeSapStageConfig.Failure("OINV rejected")
        };
        var svc     = OfflineTestDb.BuildRecoveryService(db, adapter);
        int orderId = await OfflineTestDb.SeedWaitingForRecoveryOrder(db);

        await svc.RecoverBatchAsync();

        var order = await db.OfflineFulfillmentOrders.FindAsync(orderId);
        Assert.Equal(OfflineFulfillmentState.ReconciliationRequired, order!.State);
    }

    [Fact]
    public async Task Reconciliation_Releases_Claim_So_Order_Is_Not_Stuck()
    {
        // ReconciliationRequired orders must have their claim released
        // so they don't appear as "still being recovered" (stale claim).
        using var db  = OfflineTestDb.Build();
        var adapter = new FakeOfflineSapAdapter
        {
            OrderConfig = FakeSapStageConfig.Failure("SAP is rejecting all orders")
        };
        var svc     = OfflineTestDb.BuildRecoveryService(db, adapter);
        int orderId = await OfflineTestDb.SeedWaitingForRecoveryOrder(db);

        await svc.RecoverBatchAsync();

        var order = await db.OfflineFulfillmentOrders.FindAsync(orderId);
        Assert.Equal(OfflineFulfillmentState.ReconciliationRequired, order!.State);
        Assert.Null(order.RecoveryClaimId);        // claim released
        Assert.Null(order.RecoveryClaimedAt);      // claim released
    }

    [Fact]
    public async Task Reconciliation_BinShortage_PhysicalTruth_Is_Preserved_In_ErrorMessage()
    {
        // After reconciliation, the pick records must remain unchanged.
        // The physical truth (WhsCode/BinAbsEntry/PickedQty) is never altered.
        using var db  = OfflineTestDb.Build();
        var adapter = new FakeOfflineSapAdapter
        {
            PickReplayConfig = FakeSapStageConfig.BinShortage()
        };
        var svc     = OfflineTestDb.BuildRecoveryService(db, adapter);
        int orderId = await OfflineTestDb.SeedWaitingForRecoveryOrder(
            db, whsCode: "003", binAbsEntry: 597, qty: 5m);

        await svc.RecoverBatchAsync();

        // Physical truth unchanged
        var pick = db.OfflineFulfillmentPicks.First(p => p.OfflineFulfillmentOrderId == orderId);
        Assert.Equal("003", pick.WhsCode);
        Assert.Equal(597,   pick.BinAbsEntry);
        Assert.Equal(5m,    pick.PickedQty);
        Assert.True(pick.IsConfirmed); // still confirmed — not unwound
    }

    [Fact]
    public async Task Reconciliation_NoPicks_ReconciliationReason_Is_SapPreflightFailed()
    {
        using var db  = OfflineTestDb.Build();
        var order = new OfflineFulfillmentOrder
        {
            OfflineId        = Guid.NewGuid(),
            WorkflowVersion  = FulfillmentWorkflowVersion.OfflineFulfillmentV2,
            CardCode         = "CUST",
            DocDate          = DateTime.UtcNow.Date,
            DeliveryLocation = "LOC",
            State            = OfflineFulfillmentState.WaitingForRecovery,
            RecoveryStage    = RecoveryStage.None,
            CreatedAtUtc     = DateTime.UtcNow,
            UpdatedAtUtc     = DateTime.UtcNow
        };
        db.OfflineFulfillmentOrders.Add(order);
        await db.SaveChangesAsync();

        var adapter = new FakeOfflineSapAdapter();
        var svc     = OfflineTestDb.BuildRecoveryService(db, adapter);

        await svc.RecoverBatchAsync();

        var finished = await db.OfflineFulfillmentOrders.FindAsync(order.Id);
        Assert.Equal(ReconciliationReasonCode.SapPreflightFailed, finished!.ReconciliationReason);
    }

    [Fact]
    public async Task Reconciliation_BinShortage_RaisesCorrect_ReasonCode()
    {
        using var db  = OfflineTestDb.Build();
        var adapter = new FakeOfflineSapAdapter
        {
            PickReplayConfig = FakeSapStageConfig.BinShortage()
        };
        var svc     = OfflineTestDb.BuildRecoveryService(db, adapter);
        int orderId = await OfflineTestDb.SeedWaitingForRecoveryOrder(db);

        await svc.RecoverBatchAsync();

        var order = await db.OfflineFulfillmentOrders.FindAsync(orderId);
        Assert.Equal(ReconciliationReasonCode.SapBinShortage, order!.ReconciliationReason);
    }

    [Fact]
    public async Task Reconciliation_AdapterThrows_Order_Marked_Failed()
    {
        // Unhandled exception from adapter → order marked Failed (not ReconciliationRequired).
        // ReconciliationRequired is for SAP business disagreements,
        // Failed is for unexpected infrastructure faults.
        using var db  = OfflineTestDb.Build();
        var adapter = new FakeOfflineSapAdapter
        {
            OrderConfig = FakeSapStageConfig.Crash()
        };
        var svc     = OfflineTestDb.BuildRecoveryService(db, adapter);
        int orderId = await OfflineTestDb.SeedWaitingForRecoveryOrder(db);

        await svc.RecoverBatchAsync();

        var order = await db.OfflineFulfillmentOrders.FindAsync(orderId);
        Assert.Equal(OfflineFulfillmentState.Failed, order!.State);
        Assert.Null(order.RecoveryClaimId);  // claim released even on failure
    }
}
