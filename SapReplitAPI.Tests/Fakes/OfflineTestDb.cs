using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SapReplitAPI.Models.Inventory;
using SapReplitAPI.Models.Offline;
using SapReplitAPI.Services.Neon;
using SapReplitAPI.Services.Offline;

namespace SapReplitAPI.Tests.Fakes;

/// <summary>
/// Shared test infrastructure: EF Core InMemory NeonDbContext + service factory helpers.
/// Each test builds its own isolated database instance.
/// </summary>
public static class OfflineTestDb
{
    public static NeonDbContext Build(string? name = null)
    {
        var opts = new DbContextOptionsBuilder<NeonDbContext>()
            .UseInMemoryDatabase(name ?? Guid.NewGuid().ToString())
            .Options;
        return new NeonDbContext(opts);
    }

    public static OfflineFulfillmentService BuildService(
        NeonDbContext db, bool enabled = true)
    {
        var opts = Options.Create(new OfflineFulfillmentOptions { Enabled = enabled });
        return new OfflineFulfillmentService(db, opts, NullLogger<OfflineFulfillmentService>.Instance);
    }

    /// <summary>
    /// Builds a testable recovery service with an EF-based claim seam (no ExecuteSqlRawAsync).
    /// </summary>
    public static TestableRecoveryService BuildRecoveryService(
        NeonDbContext db, FakeOfflineSapAdapter adapter, bool enabled = true)
    {
        var opts = Options.Create(new OfflineFulfillmentOptions
        {
            Enabled = enabled,
            RecoveryBatchSize = 10,
            RecoveryClaimLeaseSeconds = 120
        });
        return new TestableRecoveryService(db, opts, adapter,
            NullLogger<OfflineFulfillmentRecoveryService>.Instance);
    }

    /// <summary>Seeds a warehouse inventory row for mirrored-stock calculations.</summary>
    public static async Task SeedWarehouseInventory(
        NeonDbContext db, string itemCode, string whsCode, decimal available)
    {
        db.WarehouseInventories.Add(new WarehouseInventory
        {
            ItemCode       = itemCode,
            WhsCode        = whsCode,
            OnHand         = available,
            IsCommitted    = 0,
            AvailableToSell = available
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Seeds a bin inventory row for mirrored-stock calculations.</summary>
    public static async Task SeedBinInventory(
        NeonDbContext db, string itemCode, string whsCode, int binAbsEntry, decimal onHand)
    {
        db.BinInventories.Add(new BinInventory
        {
            ItemCode    = itemCode,
            WhsCode     = whsCode,
            BinAbsEntry = binAbsEntry,
            BinOnHand   = onHand
        });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Creates a confirmed-pick order ready for recovery (WaitingForRecovery state).
    /// Returns the orderId.
    /// </summary>
    public static async Task<int> SeedWaitingForRecoveryOrder(
        NeonDbContext db,
        string cardCode = "TEST-CARD",
        string itemCode = "ITEM-001",
        string whsCode  = "003",
        int binAbsEntry = 597,
        decimal qty     = 2m)
    {
        var order = new OfflineFulfillmentOrder
        {
            OfflineId        = Guid.NewGuid(),
            WorkflowVersion  = FulfillmentWorkflowVersion.OfflineFulfillmentV2,
            CardCode         = cardCode,
            DocDate          = DateTime.UtcNow.Date,
            DeliveryLocation = "TEST-LOCATION",
            State            = OfflineFulfillmentState.WaitingForRecovery,
            RecoveryStage    = RecoveryStage.None,
            CreatedAtUtc     = DateTime.UtcNow,
            UpdatedAtUtc     = DateTime.UtcNow
        };
        var lineId = Guid.NewGuid();
        order.Lines.Add(new OfflineFulfillmentOrderLine
        {
            RequestedLineId = lineId,
            LineSeq         = 0,
            ItemCode        = itemCode,
            RequestedQty    = qty,
            UnitPrice       = 1000m
        });
        order.Picks.Add(new OfflineFulfillmentPick
        {
            RequestedLineId  = lineId,
            ItemCode         = itemCode,
            RequestedQty     = qty,
            PickedQty        = qty,
            WhsCode          = whsCode,
            BinAbsEntry      = binAbsEntry,
            BinCode          = "TEST-BIN",
            PickerReference  = "PICKER-01",
            PickedAtUtc      = DateTime.UtcNow,
            ConfirmedAtUtc   = DateTime.UtcNow,
            OfflineConfirmId = Guid.NewGuid(),
            IsConfirmed      = true
        });
        order.Reservations.Add(new OfflineReservation
        {
            ItemCode     = itemCode,
            WhsCode      = whsCode,
            BinAbsEntry  = binAbsEntry,
            ReservedQty  = qty,
            State        = OfflineReservationState.PickConfirmed,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        });
        db.OfflineFulfillmentOrders.Add(order);
        await db.SaveChangesAsync();
        return order.Id;
    }
}

/// <summary>
/// Test subclass of OfflineFulfillmentRecoveryService that replaces the PostgreSQL
/// atomic claim (ExecuteSqlRawAsync) with an EF-based equivalent that works
/// with EF InMemory. The atomic guarantee under concurrent PostgreSQL writers is
/// verified by the design (UPDATE WHERE RecoveryClaimId IS NULL) rather than by
/// InMemory tests.
/// </summary>
public sealed class TestableRecoveryService : OfflineFulfillmentRecoveryService
{
    private readonly NeonDbContext _db;

    public TestableRecoveryService(
        NeonDbContext db,
        IOptions<OfflineFulfillmentOptions> opts,
        IOfflineSapAdapter sap,
        ILogger<OfflineFulfillmentRecoveryService> log)
        : base(db, opts, sap, log)
    {
        _db = db;
    }

    /// <summary>
    /// EF-direct claim for tests: finds unclaimed WaitingForRecovery order
    /// and sets its claim fields in a single SaveChanges call.
    /// Simulates the WHERE RecoveryClaimId IS NULL semantics.
    /// </summary>
    protected override async Task<bool> AtomicClaimAsync(
        int orderId, Guid claimId, CancellationToken ct)
    {
        var order = await _db.OfflineFulfillmentOrders
            .FirstOrDefaultAsync(o => o.Id == orderId
                && o.RecoveryClaimId == null
                && o.State == OfflineFulfillmentState.WaitingForRecovery, ct);
        if (order is null) return false;

        order.RecoveryClaimId  = claimId;
        order.RecoveryClaimedAt = DateTime.UtcNow;
        order.State            = OfflineFulfillmentState.Recovering;
        order.UpdatedAtUtc     = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    protected override async Task MarkReconciliationAsync(
        int orderId, string reason, string message, CancellationToken ct)
    {
        var order = await _db.OfflineFulfillmentOrders.FindAsync([orderId], ct);
        if (order is null) return;
        order.State                = OfflineFulfillmentState.ReconciliationRequired;
        order.ReconciliationReason = reason;
        order.ErrorMessage         = message;
        order.RecoveryClaimId      = null;
        order.RecoveryClaimedAt    = null;
        order.UpdatedAtUtc         = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    protected override async Task MarkFailedAsync(int orderId, string message, CancellationToken ct)
    {
        var order = await _db.OfflineFulfillmentOrders.FindAsync([orderId], ct);
        if (order is null) return;
        order.State             = OfflineFulfillmentState.Failed;
        order.ErrorMessage      = message;
        order.RecoveryClaimId   = null;
        order.RecoveryClaimedAt = null;
        order.UpdatedAtUtc      = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    public override async Task ReleaseStaleClaimsAsync(CancellationToken ct = default)
    {
        // EF-direct stale claim release for InMemory tests.
        // PostgreSQL raw-SQL guarantee is covered by the design; InMemory tests use LINQ.
        var cutoff = DateTime.UtcNow.AddSeconds(-120); // use hard-coded lease for tests
        var stale  = await _db.OfflineFulfillmentOrders
            .Where(o => o.State == OfflineFulfillmentState.Recovering
                     && o.RecoveryClaimedAt < cutoff
                     && o.WorkflowVersion == FulfillmentWorkflowVersion.OfflineFulfillmentV2)
            .ToListAsync(ct);
        foreach (var o in stale)
        {
            o.State             = OfflineFulfillmentState.WaitingForRecovery;
            o.RecoveryClaimId   = null;
            o.RecoveryClaimedAt = null;
            o.UpdatedAtUtc      = DateTime.UtcNow;
        }
        if (stale.Count > 0) await _db.SaveChangesAsync(ct);
    }
}
