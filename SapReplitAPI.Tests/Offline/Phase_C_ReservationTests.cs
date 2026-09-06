using SapReplitAPI.Models.Offline;
using SapReplitAPI.Services.Offline;
using SapReplitAPI.Tests.Fakes;
using Xunit;

namespace SapReplitAPI.Tests.Offline;

/// <summary>
/// Phase C: Offline reservation tests.
///
/// Critical cases:
///   - Competing reservations from two orders for the same stock (concurrency safety)
///   - Re-reservation of same slot by the same order (idempotent update)
///   - Reservation released/cancelled has no effect on another order's availability
///   - Terminal-state order blocks reservation
///   - OfflineOperationalAvailable = MirroredAvailable - ActiveReservations (excluding own order)
/// </summary>
public sealed class Phase_C_ReservationTests
{
    [Fact]
    public async Task Reserve_Succeeds_When_MirroredAvailable_Covers_Request()
    {
        using var db  = OfflineTestDb.Build();
        var svc  = OfflineTestDb.BuildService(db);
        await OfflineTestDb.SeedWarehouseInventory(db, "ITEM-A", "003", available: 10m);

        var order = await svc.CaptureAsync(Req("ITEM-A", 5m));
        var result = await svc.ReserveStockAsync(order.Id, [Slot("ITEM-A", "003", null, 5m)]);

        Assert.True(result.Success, result.Error);
    }

    [Fact]
    public async Task Reserve_Rejects_When_MirroredAvailable_Is_Insufficient()
    {
        using var db  = OfflineTestDb.Build();
        var svc  = OfflineTestDb.BuildService(db);
        await OfflineTestDb.SeedWarehouseInventory(db, "ITEM-A", "003", available: 3m);

        var order = await svc.CaptureAsync(Req("ITEM-A", 5m));
        var result = await svc.ReserveStockAsync(order.Id, [Slot("ITEM-A", "003", null, 5m)]);

        Assert.False(result.Success);
        Assert.Contains("insufficient", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Reserve_CompetingOrders_SecondIsRejected_WhenStockExhausted()
    {
        // Two orders both want 6 units but only 8 are available.
        // First order reserves 6 → 2 left. Second wants 6 → should be rejected.
        using var db  = OfflineTestDb.Build();
        var svc  = OfflineTestDb.BuildService(db);
        await OfflineTestDb.SeedWarehouseInventory(db, "ITEM-X", "003", available: 8m);

        var order1 = await svc.CaptureAsync(Req("ITEM-X", 6m));
        var res1   = await svc.ReserveStockAsync(order1.Id, [Slot("ITEM-X", "003", null, 6m)]);
        Assert.True(res1.Success, res1.Error);

        var order2 = await svc.CaptureAsync(Req("ITEM-X", 6m));
        var res2   = await svc.ReserveStockAsync(order2.Id, [Slot("ITEM-X", "003", null, 6m)]);
        Assert.False(res2.Success);
        Assert.Contains("insufficient", res2.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Reserve_CompetingOrders_BothSucceed_When_TotalFitsStock()
    {
        // Two orders each want 3 units; 6 are available. Both should succeed.
        using var db  = OfflineTestDb.Build();
        var svc  = OfflineTestDb.BuildService(db);
        await OfflineTestDb.SeedWarehouseInventory(db, "ITEM-Y", "003", available: 6m);

        var order1 = await svc.CaptureAsync(Req("ITEM-Y", 3m));
        var res1   = await svc.ReserveStockAsync(order1.Id, [Slot("ITEM-Y", "003", null, 3m)]);
        Assert.True(res1.Success, res1.Error);

        var order2 = await svc.CaptureAsync(Req("ITEM-Y", 3m));
        var res2   = await svc.ReserveStockAsync(order2.Id, [Slot("ITEM-Y", "003", null, 3m)]);
        Assert.True(res2.Success, res2.Error);
    }

    [Fact]
    public async Task Reserve_SameOrder_Reserves_Again_ExcludesOwnPriorReservation()
    {
        // Same order re-reserves same slot with larger qty.
        // Its own prior reservation must not be counted as competing stock.
        using var db  = OfflineTestDb.Build();
        var svc  = OfflineTestDb.BuildService(db);
        await OfflineTestDb.SeedWarehouseInventory(db, "ITEM-Z", "003", available: 10m);

        var order = await svc.CaptureAsync(Req("ITEM-Z", 5m));
        await svc.ReserveStockAsync(order.Id, [Slot("ITEM-Z", "003", null, 5m)]);

        // Re-reserve with more qty (own reservation is excluded from contention check)
        var reResult = await svc.ReserveStockAsync(order.Id, [Slot("ITEM-Z", "003", null, 9m)]);

        Assert.True(reResult.Success, reResult.Error);
    }

    [Fact]
    public async Task Reserve_BinManaged_Slot_Uses_BinInventory_For_Available()
    {
        using var db  = OfflineTestDb.Build();
        var svc  = OfflineTestDb.BuildService(db);
        await OfflineTestDb.SeedBinInventory(db, "ITEM-B", "003", binAbsEntry: 597, onHand: 8m);

        var order  = await svc.CaptureAsync(Req("ITEM-B", 4m));
        var result = await svc.ReserveStockAsync(order.Id, [Slot("ITEM-B", "003", 597, 4m)]);

        Assert.True(result.Success, result.Error);
    }

    [Fact]
    public async Task Reserve_BinManaged_SecondOrder_Blocked_WhenBinExhausted()
    {
        using var db  = OfflineTestDb.Build();
        var svc  = OfflineTestDb.BuildService(db);
        await OfflineTestDb.SeedBinInventory(db, "ITEM-C", "003", binAbsEntry: 491, onHand: 3m);

        var o1 = await svc.CaptureAsync(Req("ITEM-C", 3m));
        await svc.ReserveStockAsync(o1.Id, [Slot("ITEM-C", "003", 491, 3m)]);

        var o2  = await svc.CaptureAsync(Req("ITEM-C", 1m));
        var res = await svc.ReserveStockAsync(o2.Id, [Slot("ITEM-C", "003", 491, 1m)]);

        Assert.False(res.Success);
    }

    [Fact]
    public async Task Reserve_Cancelled_Reservation_Does_Not_Count_As_Active()
    {
        // Order1 reserves 5 then is cancelled → reservation state becomes Cancelled.
        // Order2 should then be able to reserve the same 5 units.
        using var db  = OfflineTestDb.Build();
        var svc  = OfflineTestDb.BuildService(db);
        await OfflineTestDb.SeedWarehouseInventory(db, "ITEM-D", "003", available: 5m);

        var o1 = await svc.CaptureAsync(Req("ITEM-D", 5m));
        await svc.ReserveStockAsync(o1.Id, [Slot("ITEM-D", "003", null, 5m)]);

        // Cancel order1's reservation manually
        var res = db.OfflineReservations.First(r => r.OfflineFulfillmentOrderId == o1.Id);
        res.State = OfflineReservationState.Cancelled;
        await db.SaveChangesAsync();

        // Order2 should succeed
        var o2  = await svc.CaptureAsync(Req("ITEM-D", 5m));
        var r2  = await svc.ReserveStockAsync(o2.Id, [Slot("ITEM-D", "003", null, 5m)]);
        Assert.True(r2.Success, r2.Error);
    }

    [Fact]
    public async Task Reserve_Rejects_When_Order_In_Cancelled_State()
    {
        using var db  = OfflineTestDb.Build();
        var svc  = OfflineTestDb.BuildService(db);
        await OfflineTestDb.SeedWarehouseInventory(db, "ITEM-E", "003", available: 10m);

        var order = await svc.CaptureAsync(Req("ITEM-E", 2m));
        var dbOrder = await db.OfflineFulfillmentOrders.FindAsync(order.Id);
        dbOrder!.State = OfflineFulfillmentState.Cancelled;
        await db.SaveChangesAsync();

        var result = await svc.ReserveStockAsync(order.Id, [Slot("ITEM-E", "003", null, 2m)]);

        Assert.False(result.Success);
        Assert.Contains("terminal", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Reserve_Transitions_Order_To_OfflinePicking()
    {
        using var db  = OfflineTestDb.Build();
        var svc  = OfflineTestDb.BuildService(db);
        await OfflineTestDb.SeedWarehouseInventory(db, "ITEM-F", "003", available: 10m);

        var order = await svc.CaptureAsync(Req("ITEM-F", 2m));
        await svc.ReserveStockAsync(order.Id, [Slot("ITEM-F", "003", null, 2m)]);

        var reloaded = await db.OfflineFulfillmentOrders.FindAsync(order.Id);
        Assert.Equal(OfflineFulfillmentState.OfflinePicking, reloaded!.State);
    }

    [Fact]
    public async Task GetMirroredStock_DataLabel_Is_LastKnown()
    {
        using var db  = OfflineTestDb.Build();
        var svc  = OfflineTestDb.BuildService(db);
        await OfflineTestDb.SeedWarehouseInventory(db, "ITEM-G", "003", available: 7m);

        var result = await svc.GetMirroredStockAsync("ITEM-G", "003", null);

        Assert.Equal("LAST KNOWN / MIRRORED STOCK", result.DataLabel);
    }

    [Fact]
    public async Task GetMirroredStock_OfflineOperationalAvailable_Subtracts_ActiveReservations()
    {
        using var db  = OfflineTestDb.Build();
        var svc  = OfflineTestDb.BuildService(db);
        await OfflineTestDb.SeedWarehouseInventory(db, "ITEM-H", "003", available: 10m);

        var order = await svc.CaptureAsync(Req("ITEM-H", 3m));
        await svc.ReserveStockAsync(order.Id, [Slot("ITEM-H", "003", null, 3m)]);

        var stock = await svc.GetMirroredStockAsync("ITEM-H", "003", null);

        Assert.Equal(10m, stock.MirroredAvailable);
        Assert.Equal(3m,  stock.ActiveOfflineReservations);
        Assert.Equal(7m,  stock.OfflineOperationalAvailable);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static CaptureOfflineFulfillmentRequest Req(string item, decimal qty) => new()
    {
        OfflineId        = Guid.NewGuid(),
        CardCode         = "CUST-TEST",
        DocDate          = DateTime.UtcNow.Date,
        DeliveryLocation = "LOC",
        Lines            = [new CaptureOfflineLine { ItemCode = item, RequestedQty = qty, UnitPrice = 500m }]
    };

    private static OfflineReservationRequest Slot(string item, string whs, int? bin, decimal qty) =>
        new() { ItemCode = item, WhsCode = whs, BinAbsEntry = bin, RequestedQty = qty };
}
