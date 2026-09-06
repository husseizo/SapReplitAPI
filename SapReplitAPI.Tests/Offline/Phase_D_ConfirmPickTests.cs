using SapReplitAPI.Models.Offline;
using SapReplitAPI.Services.Offline;
using SapReplitAPI.Tests.Fakes;
using Xunit;

namespace SapReplitAPI.Tests.Offline;

/// <summary>
/// Phase D: Offline Confirm Pick tests.
///
/// LAST HUMAN WAREHOUSE ACTION CONTRACT:
///   After ConfirmPickAsync, the picks become permanently immutable.
///   WhsCode/BinAbsEntry/PickedQty on confirmed picks are the physical fulfillment truth.
///   Recovery MUST use exactly these values — no silent reallocation.
///
/// Covers: state transition, pick stamping, reservation transition,
/// idempotency, no-picks rejection, terminal-state rejection,
/// confirmed-pick immutability, duplicate confirmation.
/// </summary>
public sealed class Phase_D_ConfirmPickTests
{
    [Fact]
    public async Task ConfirmPick_Transitions_Order_To_WaitingForRecovery()
    {
        using var db  = OfflineTestDb.Build();
        var svc  = OfflineTestDb.BuildService(db);
        var (orderId, lineId) = await SeedWithPick(db, svc);

        await svc.ConfirmPickAsync(orderId);

        var order = await db.OfflineFulfillmentOrders.FindAsync(orderId);
        Assert.Equal(OfflineFulfillmentState.WaitingForRecovery, order!.State);
    }

    [Fact]
    public async Task ConfirmPick_Stamps_All_Picks_As_Confirmed_With_ConfirmId()
    {
        using var db  = OfflineTestDb.Build();
        var svc  = OfflineTestDb.BuildService(db);
        var (orderId, lineId) = await SeedWithPick(db, svc);

        var result = await svc.ConfirmPickAsync(orderId);

        Assert.True(result.Success, result.Error);
        var picks = db.OfflineFulfillmentPicks.Where(p => p.OfflineFulfillmentOrderId == orderId).ToList();
        Assert.All(picks, p =>
        {
            Assert.True(p.IsConfirmed);
            Assert.Equal(result.ConfirmId, p.OfflineConfirmId);
            Assert.NotNull(p.ConfirmedAtUtc);
        });
    }

    [Fact]
    public async Task ConfirmPick_Transitions_Reservations_To_PickConfirmed()
    {
        using var db  = OfflineTestDb.Build();
        var svc  = OfflineTestDb.BuildService(db);
        await OfflineTestDb.SeedWarehouseInventory(db, "ITEM-A", "003", 10m);
        var (orderId, lineId) = await SeedWithPickAndReservation(db, svc, "ITEM-A", "003");

        await svc.ConfirmPickAsync(orderId);

        var reservations = db.OfflineReservations.Where(r => r.OfflineFulfillmentOrderId == orderId).ToList();
        Assert.All(reservations, r => Assert.Equal(OfflineReservationState.PickConfirmed, r.State));
    }

    [Fact]
    public async Task ConfirmPick_Is_Idempotent()
    {
        using var db  = OfflineTestDb.Build();
        var svc  = OfflineTestDb.BuildService(db);
        var (orderId, lineId) = await SeedWithPick(db, svc);

        var first  = await svc.ConfirmPickAsync(orderId);
        var second = await svc.ConfirmPickAsync(orderId);

        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.Equal(first.ConfirmId, second.ConfirmId); // same confirm ID returned
    }

    [Fact]
    public async Task ConfirmPick_Rejects_When_NoPicks()
    {
        using var db  = OfflineTestDb.Build();
        var svc  = OfflineTestDb.BuildService(db);

        var order = await svc.CaptureAsync(new CaptureOfflineFulfillmentRequest
        {
            OfflineId = Guid.NewGuid(), CardCode = "CUST", DocDate = DateTime.UtcNow.Date,
            DeliveryLocation = "LOC",
            Lines = [new CaptureOfflineLine { ItemCode = "X", RequestedQty = 1m, UnitPrice = 100m }]
        });
        var dbOrder = await db.OfflineFulfillmentOrders.FindAsync(order.Id);
        dbOrder!.State = OfflineFulfillmentState.OfflinePicking;
        await db.SaveChangesAsync();

        var result = await svc.ConfirmPickAsync(order.Id);

        Assert.False(result.Success);
        Assert.Contains("pick", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConfirmPick_Rejects_For_Cancelled_Order()
    {
        using var db  = OfflineTestDb.Build();
        var svc  = OfflineTestDb.BuildService(db);
        var (orderId, _) = await SeedWithPick(db, svc);

        var dbOrder = await db.OfflineFulfillmentOrders.FindAsync(orderId);
        dbOrder!.State = OfflineFulfillmentState.Cancelled;
        await db.SaveChangesAsync();

        var result = await svc.ConfirmPickAsync(orderId);

        Assert.False(result.Success);
        Assert.Contains("terminal", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConfirmedPick_WhsCode_Is_Immutable_After_Confirm()
    {
        // Proves the physical truth immutability contract.
        // After confirm, the WhsCode on each pick must not change.
        using var db  = OfflineTestDb.Build();
        var svc  = OfflineTestDb.BuildService(db);
        var (orderId, lineId) = await SeedWithPick(db, svc, whsCode: "003", pickedQty: 5m);
        await svc.ConfirmPickAsync(orderId);

        // Attempt to record a new pick after confirm — service must reject
        var result = await svc.RecordPickAsync(orderId,
            [new OfflinePickRequest
            {
                RequestedLineId = lineId,
                ItemCode = "ITEM-A",
                RequestedQty = 5m,
                PickedQty = 5m,
                WhsCode = "002",  // different warehouse — this is the "silent reallocation" we must prevent
                PickerReference = "PICKER-01"
            }]);

        Assert.False(result.Success);

        // The pick in the database must still have the original WhsCode
        var pick = db.OfflineFulfillmentPicks.First(p => p.OfflineFulfillmentOrderId == orderId);
        Assert.Equal("003", pick.WhsCode);
    }

    [Fact]
    public async Task ConfirmedPick_PickedQty_Is_Immutable()
    {
        using var db  = OfflineTestDb.Build();
        var svc  = OfflineTestDb.BuildService(db);
        var (orderId, lineId) = await SeedWithPick(db, svc, pickedQty: 5m);
        await svc.ConfirmPickAsync(orderId);

        await svc.RecordPickAsync(orderId,
            [new OfflinePickRequest
            {
                RequestedLineId = lineId,
                ItemCode = "ITEM-A",
                RequestedQty = 5m,
                PickedQty = 999m,
                WhsCode = "003",
                PickerReference = "PICKER-02"
            }]);

        var pick = db.OfflineFulfillmentPicks.First(p => p.OfflineFulfillmentOrderId == orderId);
        Assert.Equal(5m, pick.PickedQty); // unchanged
    }

    [Fact]
    public async Task ConfirmPick_MultiplePicks_AllStamped_WithSameConfirmId()
    {
        using var db  = OfflineTestDb.Build();
        var svc  = OfflineTestDb.BuildService(db);

        var lineId1 = Guid.NewGuid();
        var lineId2 = Guid.NewGuid();
        var order = await svc.CaptureAsync(new CaptureOfflineFulfillmentRequest
        {
            OfflineId = Guid.NewGuid(), CardCode = "CUST", DocDate = DateTime.UtcNow.Date,
            DeliveryLocation = "LOC",
            Lines =
            [
                new CaptureOfflineLine { RequestedLineId = lineId1, ItemCode = "ITEM-A", RequestedQty = 2m, UnitPrice = 100m },
                new CaptureOfflineLine { RequestedLineId = lineId2, ItemCode = "ITEM-B", RequestedQty = 1m, UnitPrice = 200m }
            ]
        });
        var dbOrder = await db.OfflineFulfillmentOrders.FindAsync(order.Id);
        dbOrder!.State = OfflineFulfillmentState.OfflinePicking;
        await db.SaveChangesAsync();
        var dbLines = db.OfflineFulfillmentOrderLines.Where(l => l.OfflineFulfillmentOrderId == order.Id).ToList();

        await svc.RecordPickAsync(order.Id,
        [
            new OfflinePickRequest { RequestedLineId = dbLines[0].RequestedLineId, ItemCode = "ITEM-A", RequestedQty = 2m, PickedQty = 2m, WhsCode = "003", PickerReference = "P1" },
            new OfflinePickRequest { RequestedLineId = dbLines[1].RequestedLineId, ItemCode = "ITEM-B", RequestedQty = 1m, PickedQty = 1m, WhsCode = "003", PickerReference = "P1" }
        ]);

        var result = await svc.ConfirmPickAsync(order.Id);

        var picks = db.OfflineFulfillmentPicks.Where(p => p.OfflineFulfillmentOrderId == order.Id).ToList();
        Assert.Equal(2, picks.Count);
        Assert.All(picks, p =>
        {
            Assert.True(p.IsConfirmed);
            Assert.Equal(result.ConfirmId, p.OfflineConfirmId);
        });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task<(int, Guid)> SeedWithPick(
        SapReplitAPI.Services.Neon.NeonDbContext db,
        OfflineFulfillmentService svc,
        string whsCode = "003", decimal pickedQty = 5m)
    {
        var lineId = Guid.NewGuid();
        var order = await svc.CaptureAsync(new CaptureOfflineFulfillmentRequest
        {
            OfflineId = Guid.NewGuid(), CardCode = "CUST", DocDate = DateTime.UtcNow.Date,
            DeliveryLocation = "LOC",
            Lines = [new CaptureOfflineLine { RequestedLineId = lineId, ItemCode = "ITEM-A", RequestedQty = pickedQty, UnitPrice = 100m }]
        });
        var dbOrder = await db.OfflineFulfillmentOrders.FindAsync(order.Id);
        dbOrder!.State = OfflineFulfillmentState.OfflinePicking;
        await db.SaveChangesAsync();

        var dbLine = db.OfflineFulfillmentOrderLines.First(l => l.OfflineFulfillmentOrderId == order.Id);
        await svc.RecordPickAsync(order.Id,
        [new OfflinePickRequest { RequestedLineId = dbLine.RequestedLineId, ItemCode = "ITEM-A",
            RequestedQty = pickedQty, PickedQty = pickedQty, WhsCode = whsCode, PickerReference = "P1" }]);

        return (order.Id, dbLine.RequestedLineId);
    }

    private static async Task<(int, Guid)> SeedWithPickAndReservation(
        SapReplitAPI.Services.Neon.NeonDbContext db,
        OfflineFulfillmentService svc,
        string item, string whs)
    {
        var (orderId, lineId) = await SeedWithPick(db, svc);
        var order = await db.OfflineFulfillmentOrders.FindAsync(orderId);
        order!.State = OfflineFulfillmentState.OfflinePicking;
        db.OfflineReservations.Add(new SapReplitAPI.Models.Offline.OfflineReservation
        {
            OfflineFulfillmentOrderId = orderId,
            ItemCode = item, WhsCode = whs,
            ReservedQty = 5m, State = OfflineReservationState.Reserved,
            CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        return (orderId, lineId);
    }
}
