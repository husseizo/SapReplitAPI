using Microsoft.Extensions.Options;
using SapReplitAPI.Models.Offline;
using SapReplitAPI.Services.Offline;
using SapReplitAPI.Tests.Fakes;
using Xunit;

namespace SapReplitAPI.Tests.Offline;

/// <summary>
/// Feature flag tests: OfflineFulfillmentV2:Enabled = false (default).
///
/// When disabled:
///   - RecoveryJob.Execute() is a no-op (no SAP calls, no DB mutations)
///   - All service operations still function (flag is enforced at controller level)
///   - V1 PendingOrderSyncJob is unaffected regardless of flag value
///
/// When enabled:
///   - Recovery job processes WaitingForRecovery orders
///   - Service creates capture records normally
/// </summary>
public sealed class FeatureFlagTests
{
    [Fact]
    public async Task FeatureDisabled_RecoveryJob_IsNoop_ZeroSapCalls()
    {
        using var db  = OfflineTestDb.Build();
        await OfflineTestDb.SeedWaitingForRecoveryOrder(db); // order waiting for recovery

        var adapter = new FakeOfflineSapAdapter();
        // Build recovery service with Enabled=false
        var svc = OfflineTestDb.BuildRecoveryService(db, adapter, enabled: false);

        // Simulate job Execute logic (feature guard is in the job, not service)
        // The job checks Enabled before calling RecoverBatchAsync.
        // Here we test the guard: if Enabled=false, no batch is run.
        // We manually test the guard via the job.
        var opts = Options.Create(new OfflineFulfillmentOptions { Enabled = false });
        var job  = new Jobs.OfflineFulfillmentRecoveryJob(svc, opts,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Jobs.OfflineFulfillmentRecoveryJob>.Instance);

        var ctx = new TestJobContext();
        await job.Execute(ctx);

        // No SAP calls when disabled
        Assert.Equal(0, adapter.OrderCallCount);
        Assert.Equal(0, adapter.PickListCallCount);
        Assert.Equal(0, adapter.DeliveryCallCount);
        Assert.Equal(0, adapter.InvoiceCallCount);

        // Order still in WaitingForRecovery state — job made no mutations
        var order = db.OfflineFulfillmentOrders.First();
        Assert.Equal(OfflineFulfillmentState.WaitingForRecovery, order.State);
    }

    [Fact]
    public async Task FeatureEnabled_RecoveryJob_ProcessesOrders()
    {
        using var db  = OfflineTestDb.Build();
        await OfflineTestDb.SeedWaitingForRecoveryOrder(db);

        var adapter = new FakeOfflineSapAdapter();
        var svc  = OfflineTestDb.BuildRecoveryService(db, adapter, enabled: true);
        var opts = Options.Create(new OfflineFulfillmentOptions { Enabled = true, RecoveryBatchSize = 5 });
        var job  = new Jobs.OfflineFulfillmentRecoveryJob(svc, opts,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Jobs.OfflineFulfillmentRecoveryJob>.Instance);

        var ctx = new TestJobContext();
        await job.Execute(ctx);

        Assert.Equal(1, adapter.OrderCallCount);
        var order = db.OfflineFulfillmentOrders.First();
        Assert.Equal(OfflineFulfillmentState.Completed, order.State);
    }

    [Fact]
    public async Task FeatureDisabled_CaptureService_StillWorks()
    {
        // Feature flag does not block data capture — it only blocks recovery.
        // (Controller returns 503 but service-level capture is independent.)
        using var db  = OfflineTestDb.Build();
        var svc  = OfflineTestDb.BuildService(db, enabled: false);

        // Service CaptureAsync does not check the flag — controller does.
        var order = await svc.CaptureAsync(new CaptureOfflineFulfillmentRequest
        {
            OfflineId = Guid.NewGuid(), CardCode = "CUST", DocDate = DateTime.UtcNow.Date,
            DeliveryLocation = "LOC",
            Lines = [new CaptureOfflineLine { ItemCode = "X", RequestedQty = 1m, UnitPrice = 100m }]
        });
        Assert.NotNull(order);
    }

    [Fact]
    public void FeatureDisabled_DefaultValue_Is_False()
    {
        var opts = new OfflineFulfillmentOptions();
        Assert.False(opts.Enabled);
    }

    [Fact]
    public void FeatureFlagOptions_Section_Is_OfflineFulfillmentV2()
    {
        Assert.Equal("OfflineFulfillmentV2", OfflineFulfillmentOptions.Section);
    }

    [Fact]
    public async Task FeatureDisabled_RecoveryJob_Does_Not_Transition_Order_State()
    {
        using var db  = OfflineTestDb.Build();
        int orderId = await OfflineTestDb.SeedWaitingForRecoveryOrder(db);

        var adapter = new FakeOfflineSapAdapter();
        var svc     = OfflineTestDb.BuildRecoveryService(db, adapter, enabled: false);
        var opts    = Options.Create(new OfflineFulfillmentOptions { Enabled = false });
        var job     = new Jobs.OfflineFulfillmentRecoveryJob(svc, opts,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Jobs.OfflineFulfillmentRecoveryJob>.Instance);

        await job.Execute(new TestJobContext());

        var order = await db.OfflineFulfillmentOrders.FindAsync(orderId);
        Assert.Equal(OfflineFulfillmentState.WaitingForRecovery, order!.State);
        Assert.Null(order.RecoveryClaimId);
    }
}

/// <summary>Minimal IJobExecutionContext stub for unit tests (Quartz 3.x).</summary>
internal sealed class TestJobContext : Quartz.IJobExecutionContext
{
    public string              FireInstanceId       => Guid.NewGuid().ToString();
    public CancellationToken   CancellationToken    => CancellationToken.None;
    public Quartz.IScheduler   Scheduler            => throw new NotImplementedException();
    public Quartz.ITrigger     Trigger              => throw new NotImplementedException();
    public Quartz.IJobDetail   JobDetail            => throw new NotImplementedException();
    public Quartz.ICalendar?   Calendar             => null;
    public Quartz.IJob         JobInstance          => throw new NotImplementedException();
    public Quartz.TriggerKey   RecoveringTriggerKey => throw new NotImplementedException();
    public Quartz.JobDataMap   JobDataMap           => new();
    public Quartz.JobDataMap   MergedJobDataMap     => new();
    public bool   Recovering                        => false;
    public int    RefireCount                       => 0;
    public DateTimeOffset  FireTimeUtc              => DateTimeOffset.UtcNow;
    public DateTimeOffset? ScheduledFireTimeUtc     => null;
    public DateTimeOffset? NextFireTimeUtc          => null;
    public DateTimeOffset? PreviousFireTimeUtc      => null;
    public TimeSpan        JobRunTime               => TimeSpan.Zero;
    public object?         Result { get; set; }
    public void Put(object key, object objectValue) { }
    public object? Get(object key) => null;
}
