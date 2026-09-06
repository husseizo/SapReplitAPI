using SapReplitAPI.Models.Offline;
using SapReplitAPI.Services.Offline;
using SapReplitAPI.Tests.Fakes;
using Xunit;

namespace SapReplitAPI.Tests.Offline;

/// <summary>
/// Phase C: Offline order capture tests.
/// Verifies idempotency, DeliveryLocation preservation, WorkflowVersion discriminator,
/// and state of a freshly-captured order.
/// </summary>
public sealed class Phase_C_CaptureTests
{
    [Fact]
    public async Task Capture_Creates_Order_With_V2_WorkflowVersion()
    {
        using var db  = OfflineTestDb.Build();
        var svc  = OfflineTestDb.BuildService(db);
        var req  = MakeRequest();

        var order = await svc.CaptureAsync(req);

        Assert.Equal(FulfillmentWorkflowVersion.OfflineFulfillmentV2, order.WorkflowVersion);
    }

    [Fact]
    public async Task Capture_Creates_Order_In_PendingOffline_State()
    {
        using var db  = OfflineTestDb.Build();
        var svc  = OfflineTestDb.BuildService(db);

        var order = await svc.CaptureAsync(MakeRequest());

        Assert.Equal(OfflineFulfillmentState.PendingOffline, order.State);
    }

    [Fact]
    public async Task Capture_Preserves_DeliveryLocation()
    {
        using var db  = OfflineTestDb.Build();
        var svc  = OfflineTestDb.BuildService(db);
        var req  = MakeRequest("CUST-TEST", deliveryLocation: "Mikocheni-side");

        var order = await svc.CaptureAsync(req);

        Assert.Equal("Mikocheni-side", order.DeliveryLocation);
    }

    [Fact]
    public async Task Capture_Creates_Lines_With_RequestedLineId()
    {
        using var db  = OfflineTestDb.Build();
        var svc  = OfflineTestDb.BuildService(db);

        var order = await svc.CaptureAsync(MakeRequest());

        Assert.Equal(2, order.Lines.Count);
        Assert.All(order.Lines, l => Assert.NotEqual(Guid.Empty, l.RequestedLineId));
    }

    [Fact]
    public async Task Capture_Is_Idempotent_For_Same_OfflineId()
    {
        using var db  = OfflineTestDb.Build();
        var svc  = OfflineTestDb.BuildService(db);
        var offlineId = Guid.NewGuid();
        var req  = MakeRequest(offlineId: offlineId);

        // First call
        var first  = await svc.CaptureAsync(req);
        // Second call — same OfflineId
        var second = await svc.CaptureAsync(req);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(1, db.OfflineFulfillmentOrders.Count());
    }

    [Fact]
    public async Task Capture_Different_OfflineIds_Create_Separate_Orders()
    {
        using var db  = OfflineTestDb.Build();
        var svc  = OfflineTestDb.BuildService(db);

        await svc.CaptureAsync(MakeRequest(offlineId: Guid.NewGuid()));
        await svc.CaptureAsync(MakeRequest(offlineId: Guid.NewGuid()));

        Assert.Equal(2, db.OfflineFulfillmentOrders.Count());
    }

    [Fact]
    public async Task Capture_RecoveryStage_DefaultsTo_None()
    {
        using var db  = OfflineTestDb.Build();
        var svc  = OfflineTestDb.BuildService(db);

        var order = await svc.CaptureAsync(MakeRequest());

        Assert.Equal(RecoveryStage.None, order.RecoveryStage);
    }

    [Fact]
    public async Task Capture_Does_Not_Create_V1_PendingOrder()
    {
        using var db  = OfflineTestDb.Build();
        var svc  = OfflineTestDb.BuildService(db);

        await svc.CaptureAsync(MakeRequest());

        Assert.Empty(db.PendingOrders);
    }

    private static CaptureOfflineFulfillmentRequest MakeRequest(
        string cardCode = "CUST-TEST",
        string deliveryLocation = "TEST-LOC",
        Guid? offlineId = null) => new()
    {
        OfflineId        = offlineId ?? Guid.NewGuid(),
        CardCode         = cardCode,
        DocDate          = DateTime.UtcNow.Date,
        DocCurrency      = "TZS",
        DeliveryLocation = deliveryLocation,
        Lines            =
        [
            new CaptureOfflineLine { ItemCode = "ITEM-A", RequestedQty = 5m, UnitPrice = 1000m },
            new CaptureOfflineLine { ItemCode = "ITEM-B", RequestedQty = 2m, UnitPrice = 500m  }
        ]
    };
}
