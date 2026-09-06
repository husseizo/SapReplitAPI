using SapReplitAPI.Models.Offline;
using SapReplitAPI.Services.Offline;
using SapReplitAPI.Tests.Fakes;
using Xunit;

namespace SapReplitAPI.Tests.Offline;

/// <summary>
/// Phase D: Pick recording tests.
///
/// Covers: multi-WHS picks, multi-bin picks, pick update before confirm,
/// invalid qty rejection, over-pick (allowed), terminal-state rejection,
/// post-confirm immutability.
/// </summary>
public sealed class Phase_D_PickTests
{
    [Fact]
    public async Task RecordPick_Creates_Pick_Record()
    {
        using var db  = OfflineTestDb.Build();
        var svc  = OfflineTestDb.BuildService(db);
        var (orderId, lineId) = await SetupPickingOrder(db, svc, "ITEM-A", "003");

        var result = await svc.RecordPickAsync(orderId, [Pick(lineId, "ITEM-A", "003", null, 5m)]);

        Assert.True(result.Success, result.Error);
        Assert.Single(db.OfflineFulfillmentPicks.Where(p => p.OfflineFulfillmentOrderId == orderId));
    }

    [Fact]
    public async Task RecordPick_Updates_ExistingPick_For_SameSlot()
    {
        using var db  = OfflineTestDb.Build();
        var svc  = OfflineTestDb.BuildService(db);
        var (orderId, lineId) = await SetupPickingOrder(db, svc, "ITEM-A", "003");

        await svc.RecordPickAsync(orderId, [Pick(lineId, "ITEM-A", "003", null, 4m)]);
        await svc.RecordPickAsync(orderId, [Pick(lineId, "ITEM-A", "003", null, 5m)]); // update

        var pick = db.OfflineFulfillmentPicks.Single(p => p.OfflineFulfillmentOrderId == orderId);
        Assert.Equal(5m, pick.PickedQty); // updated, not duplicated
    }

    [Fact]
    public async Task RecordPick_MultiWhs_Creates_Multiple_Pick_Records()
    {
        // One item split across two warehouses
        using var db  = OfflineTestDb.Build();
        var svc  = OfflineTestDb.BuildService(db);
        var (orderId, lineId) = await SetupPickingOrder(db, svc, "ITEM-MULTI", "003");

        var result = await svc.RecordPickAsync(orderId,
        [
            Pick(lineId, "ITEM-MULTI", "003", null, 3m),
            Pick(lineId, "ITEM-MULTI", "002", null, 2m)
        ]);

        Assert.True(result.Success, result.Error);
        Assert.Equal(2, db.OfflineFulfillmentPicks.Count(p => p.OfflineFulfillmentOrderId == orderId));
    }

    [Fact]
    public async Task RecordPick_MultiBin_Creates_Multiple_Pick_Records()
    {
        // One item split across two bins in same warehouse
        using var db  = OfflineTestDb.Build();
        var svc  = OfflineTestDb.BuildService(db);
        var (orderId, lineId) = await SetupPickingOrder(db, svc, "ITEM-BINS", "003");

        var result = await svc.RecordPickAsync(orderId,
        [
            Pick(lineId, "ITEM-BINS", "003", 491, 2m),
            Pick(lineId, "ITEM-BINS", "003", 597, 3m)
        ]);

        Assert.True(result.Success, result.Error);
        Assert.Equal(2, db.OfflineFulfillmentPicks.Count(p => p.OfflineFulfillmentOrderId == orderId));
    }

    [Fact]
    public async Task RecordPick_Rejects_ZeroPickedQty()
    {
        using var db  = OfflineTestDb.Build();
        var svc  = OfflineTestDb.BuildService(db);
        var (orderId, lineId) = await SetupPickingOrder(db, svc, "ITEM-A", "003");

        var result = await svc.RecordPickAsync(orderId, [Pick(lineId, "ITEM-A", "003", null, 0m)]);

        Assert.False(result.Success);
        Assert.Contains("PickedQty", result.Error);
    }

    [Fact]
    public async Task RecordPick_Rejects_NegativePickedQty()
    {
        using var db  = OfflineTestDb.Build();
        var svc  = OfflineTestDb.BuildService(db);
        var (orderId, lineId) = await SetupPickingOrder(db, svc, "ITEM-A", "003");

        var result = await svc.RecordPickAsync(orderId, [Pick(lineId, "ITEM-A", "003", null, -1m)]);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task RecordPick_Allows_OverPick_GreaterThan_RequestedQty()
    {
        // Physical truth: picker grabbed more than originally requested.
        // SAP will handle the overpick at recovery time.
        using var db  = OfflineTestDb.Build();
        var svc  = OfflineTestDb.BuildService(db);
        var (orderId, lineId) = await SetupPickingOrder(db, svc, "ITEM-A", "003", requestedQty: 2m);

        var result = await svc.RecordPickAsync(orderId, [Pick(lineId, "ITEM-A", "003", null, 5m)]);

        Assert.True(result.Success, result.Error);
    }

    [Fact]
    public async Task RecordPick_Rejects_After_OfflinePickConfirmed()
    {
        using var db  = OfflineTestDb.Build();
        var svc  = OfflineTestDb.BuildService(db);
        var (orderId, lineId) = await SetupPickingOrder(db, svc, "ITEM-A", "003");

        await svc.RecordPickAsync(orderId, [Pick(lineId, "ITEM-A", "003", null, 5m)]);
        await svc.ConfirmPickAsync(orderId);

        var result = await svc.RecordPickAsync(orderId, [Pick(lineId, "ITEM-A", "003", null, 3m)]);

        Assert.False(result.Success);
        Assert.Contains("confirmed", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RecordPick_Rejects_For_Cancelled_Order()
    {
        using var db  = OfflineTestDb.Build();
        var svc  = OfflineTestDb.BuildService(db);
        var (orderId, lineId) = await SetupPickingOrder(db, svc, "ITEM-A", "003");

        var dbOrder = await db.OfflineFulfillmentOrders.FindAsync(orderId);
        dbOrder!.State = OfflineFulfillmentState.Cancelled;
        await db.SaveChangesAsync();

        var result = await svc.RecordPickAsync(orderId, [Pick(lineId, "ITEM-A", "003", null, 5m)]);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task RecordPick_Preserves_WhsCode_And_BinAbsEntry_As_PhysicalTruth()
    {
        using var db  = OfflineTestDb.Build();
        var svc  = OfflineTestDb.BuildService(db);
        var (orderId, lineId) = await SetupPickingOrder(db, svc, "ITEM-T", "003");

        await svc.RecordPickAsync(orderId, [Pick(lineId, "ITEM-T", "003", 597, 4m)]);

        var pick = db.OfflineFulfillmentPicks.Single(p => p.OfflineFulfillmentOrderId == orderId);
        Assert.Equal("003", pick.WhsCode);
        Assert.Equal(597, pick.BinAbsEntry);
        Assert.Equal(4m, pick.PickedQty);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task<(int orderId, Guid lineId)> SetupPickingOrder(
        SapReplitAPI.Services.Neon.NeonDbContext db,
        OfflineFulfillmentService svc,
        string itemCode, string whsCode,
        decimal requestedQty = 5m)
    {
        var lineId = Guid.NewGuid();
        var order = await svc.CaptureAsync(new CaptureOfflineFulfillmentRequest
        {
            OfflineId        = Guid.NewGuid(),
            CardCode         = "CUST-TEST",
            DocDate          = DateTime.UtcNow.Date,
            DeliveryLocation = "TEST-LOC",
            Lines            = [new CaptureOfflineLine
            {
                RequestedLineId = lineId,
                ItemCode        = itemCode,
                RequestedQty    = requestedQty,
                UnitPrice       = 1000m
            }]
        });
        // Set state to OfflinePicking so picks can be recorded
        var dbOrder = await db.OfflineFulfillmentOrders.FindAsync(order.Id);
        dbOrder!.State = OfflineFulfillmentState.OfflinePicking;
        await db.SaveChangesAsync();

        // Re-fetch the line's RequestedLineId
        var line = db.OfflineFulfillmentOrderLines.First(l => l.OfflineFulfillmentOrderId == order.Id);
        return (order.Id, line.RequestedLineId);
    }

    private static OfflinePickRequest Pick(
        Guid lineId, string item, string whs, int? bin, decimal pickedQty,
        string picker = "PICKER-01") =>
        new()
        {
            RequestedLineId = lineId,
            ItemCode        = item,
            RequestedQty    = pickedQty,
            PickedQty       = pickedQty,
            WhsCode         = whs,
            BinAbsEntry     = bin,
            BinCode         = bin.HasValue ? $"BIN-{bin}" : null,
            PickerReference = picker
        };
}
