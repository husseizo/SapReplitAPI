using SapReplitAPI.Models.Offline;
using SapReplitAPI.Models.Orde_Models;
using SapReplitAPI.Models.Pending;
using SapReplitAPI.Services;
using SapReplitAPI.Tests.Fakes;
using Xunit;

namespace SapReplitAPI.Tests.V1Regression;

/// <summary>
/// GATE 0 — Legacy Pending V1 Regression Tests.
///
/// These tests verify that every observable V1 behavior contract is unchanged
/// after adding V2 tables, V2 services, and V2 Quartz job to the codebase.
/// If any test here fails, Phases A–G are blocked.
///
/// Tests cover:
///   - V1 model contract (no WorkflowVersion, no DeliveryLocation)
///   - SavePendingAsync flow and ReplitId format
///   - GetEligibleAsync filter semantics (Status=Pending, NextRetryAt)
///   - Retry backoff sequence and MaxRetries=8 threshold
///   - MarkFailedAsync immediate fail-without-retry
///   - V1 service isolation: never touches V2 tables
///   - V2 records never surface via V1 queries
/// </summary>
public sealed class PendingOrderV1RegressionTests
{
    // ── V1 Model Contract ──────────────────────────────────────────────────────

    [Fact]
    public void V1_PendingOrder_Has_No_WorkflowVersion_Field()
    {
        var t = typeof(PendingOrder);
        Assert.Null(t.GetProperty("WorkflowVersion"));
    }

    [Fact]
    public void V1_PendingOrder_Has_No_DeliveryLocation_Field()
    {
        var t = typeof(PendingOrder);
        Assert.Null(t.GetProperty("DeliveryLocation"));
    }

    [Fact]
    public void V1_PendingOrder_Status_DefaultIs_Pending()
    {
        var order = new PendingOrder();
        Assert.Equal("Pending", order.Status);
    }

    [Fact]
    public void V1_ReplitId_Format_StartsWith_OR()
    {
        var id = PendingOrderService.NewReplitId();
        Assert.StartsWith("OR-", id);
        Assert.DoesNotContain("ZF-", id);
    }

    // ── SavePendingAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task V1_SavePendingAsync_Creates_Pending_Record()
    {
        using var db  = OfflineTestDb.Build();
        var svc = BuildV1Service(db);
        var dto = MakeDto();
        var replitId = PendingOrderService.NewReplitId();

        var order = await svc.SavePendingAsync(dto, replitId);

        Assert.Equal("Pending", order.Status);
        Assert.Equal(replitId, order.ReplitId);
        Assert.Equal(dto.CardCode, order.CardCode);
        Assert.Equal(2, order.Lines.Count);
        Assert.Equal(0, order.RetryCount);
        Assert.Null(order.NextRetryAt);
    }

    [Fact]
    public async Task V1_SavePendingAsync_DefaultWhsCode_Is_001()
    {
        using var db  = OfflineTestDb.Build();
        var svc = BuildV1Service(db);
        var dto = MakeDto();
        dto.Lines[0].WhsCode = null; // omitted → should default to "001"

        var order = await svc.SavePendingAsync(dto, PendingOrderService.NewReplitId());

        Assert.Equal("001", order.Lines[0].WhsCode);
    }

    // ── GetEligibleAsync filter ────────────────────────────────────────────────

    [Fact]
    public async Task V1_GetEligibleAsync_Returns_Pending_Without_NextRetryAt()
    {
        using var db  = OfflineTestDb.Build();
        var svc = BuildV1Service(db);
        await svc.SavePendingAsync(MakeDto(), PendingOrderService.NewReplitId());

        var eligible = await svc.GetEligibleAsync();

        Assert.Single(eligible);
    }

    [Fact]
    public async Task V1_GetEligibleAsync_Excludes_NextRetryAt_In_Future()
    {
        using var db  = OfflineTestDb.Build();
        var svc = BuildV1Service(db);
        var order = await svc.SavePendingAsync(MakeDto(), PendingOrderService.NewReplitId());
        // Simulate backoff: set NextRetryAt to future
        var saved = await db.PendingOrders.FindAsync(order.Id);
        saved!.NextRetryAt = DateTime.UtcNow.AddMinutes(5);
        await db.SaveChangesAsync();

        var eligible = await svc.GetEligibleAsync();

        Assert.Empty(eligible);
    }

    [Fact]
    public async Task V1_GetEligibleAsync_Excludes_Draft_And_Synced_And_Failed()
    {
        using var db  = OfflineTestDb.Build();
        var svc = BuildV1Service(db);

        // Add records in non-eligible states
        db.PendingOrders.Add(new PendingOrder { ReplitId = "OR-DRAFT",  Status = "Draft",  CreatedAt = DateTime.UtcNow });
        db.PendingOrders.Add(new PendingOrder { ReplitId = "OR-SYNCED", Status = "Synced", CreatedAt = DateTime.UtcNow });
        db.PendingOrders.Add(new PendingOrder { ReplitId = "OR-FAILED", Status = "Failed", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var eligible = await svc.GetEligibleAsync();

        Assert.Empty(eligible);
    }

    [Fact]
    public async Task V1_GetEligibleAsync_Does_Not_Return_V2_Offline_Orders()
    {
        using var db  = OfflineTestDb.Build();
        var svc = BuildV1Service(db);

        // Add a V2 order in WaitingForRecovery — must NEVER appear in V1 eligible list
        db.OfflineFulfillmentOrders.Add(new OfflineFulfillmentOrder
        {
            OfflineId       = Guid.NewGuid(),
            WorkflowVersion = FulfillmentWorkflowVersion.OfflineFulfillmentV2,
            CardCode        = "V2-CARD",
            State           = OfflineFulfillmentState.WaitingForRecovery,
            DocDate         = DateTime.UtcNow,
            CreatedAtUtc    = DateTime.UtcNow,
            UpdatedAtUtc    = DateTime.UtcNow
        });
        // Add a V1 pending order
        await svc.SavePendingAsync(MakeDto(), PendingOrderService.NewReplitId());

        var eligible = await svc.GetEligibleAsync();

        Assert.Single(eligible);               // only the V1 order
        Assert.StartsWith("OR-", eligible[0].ReplitId);
    }

    // ── Retry backoff ──────────────────────────────────────────────────────────

    [Fact]
    public async Task V1_RecordRejectionAsync_AppliesBackoff_Sequence()
    {
        // Backoff: [30, 60, 120, 300, 600, 1200, 1800, 3600]
        using var db  = OfflineTestDb.Build();
        var svc = BuildV1Service(db);
        var order = await svc.SavePendingAsync(MakeDto(), PendingOrderService.NewReplitId());

        // First rejection: RetryCount=1, delay=30s
        await svc.RecordRejectionAsync(order.Id, "err");
        var reloaded = await db.PendingOrders.FindAsync(order.Id);
        Assert.Equal(1, reloaded!.RetryCount);
        Assert.Equal("Pending", reloaded.Status);
        Assert.NotNull(reloaded.NextRetryAt);
        Assert.InRange(reloaded.NextRetryAt.Value,
            DateTime.UtcNow.AddSeconds(25), DateTime.UtcNow.AddSeconds(35));
    }

    [Fact]
    public async Task V1_RecordRejectionAsync_MarksFailedAfter_8_Retries()
    {
        using var db  = OfflineTestDb.Build();
        var svc = BuildV1Service(db);
        var order = await svc.SavePendingAsync(MakeDto(), PendingOrderService.NewReplitId());

        for (int i = 0; i < 8; i++)
            await svc.RecordRejectionAsync(order.Id, $"err-{i}");

        var reloaded = await db.PendingOrders.FindAsync(order.Id);
        Assert.Equal("Failed", reloaded!.Status);
        Assert.Equal(8, reloaded.RetryCount);
    }

    // ── MarkFailedAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task V1_MarkFailedAsync_ImmediatelyFails_Without_Backoff()
    {
        using var db  = OfflineTestDb.Build();
        var svc = BuildV1Service(db);
        var order = await svc.SavePendingAsync(MakeDto(), PendingOrderService.NewReplitId());

        await svc.MarkFailedAsync(order.Id, "Business rejection");

        var reloaded = await db.PendingOrders.FindAsync(order.Id);
        Assert.Equal("Failed", reloaded!.Status);
        Assert.Equal(0, reloaded.RetryCount);  // no retry incremented
        Assert.Null(reloaded.NextRetryAt);     // no backoff
    }

    // ── V1 / V2 table isolation ────────────────────────────────────────────────

    [Fact]
    public async Task V1_PendingOrderService_Never_Queries_OfflineFulfillmentOrders()
    {
        // Arrange: populate ONLY V2 table, V1 table is empty
        using var db  = OfflineTestDb.Build();
        var svc = BuildV1Service(db);
        db.OfflineFulfillmentOrders.Add(new OfflineFulfillmentOrder
        {
            OfflineId       = Guid.NewGuid(),
            WorkflowVersion = FulfillmentWorkflowVersion.OfflineFulfillmentV2,
            CardCode        = "V2-CARD",
            State           = OfflineFulfillmentState.PendingOffline,
            DocDate         = DateTime.UtcNow,
            CreatedAtUtc    = DateTime.UtcNow,
            UpdatedAtUtc    = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        // V1 service must see zero eligible orders
        var eligible = await svc.GetEligibleAsync();
        var all      = await svc.GetAllAsync();

        Assert.Empty(eligible);
        Assert.Empty(all);
    }

    [Fact]
    public async Task V2_Table_Presence_Does_Not_Affect_V1_Counts()
    {
        using var db  = OfflineTestDb.Build();
        var svc = BuildV1Service(db);

        // Seed 3 V1 pending orders
        for (int i = 0; i < 3; i++)
            await svc.SavePendingAsync(MakeDto($"CARD-{i}"), PendingOrderService.NewReplitId());

        // Seed 5 V2 orders in various states
        for (int i = 0; i < 5; i++)
            db.OfflineFulfillmentOrders.Add(new OfflineFulfillmentOrder
            {
                OfflineId       = Guid.NewGuid(),
                WorkflowVersion = FulfillmentWorkflowVersion.OfflineFulfillmentV2,
                CardCode        = $"V2-{i}",
                State           = OfflineFulfillmentState.WaitingForRecovery,
                DocDate         = DateTime.UtcNow,
                CreatedAtUtc    = DateTime.UtcNow,
                UpdatedAtUtc    = DateTime.UtcNow
            });
        await db.SaveChangesAsync();

        var eligible = await svc.GetEligibleAsync(limit: 100);
        var all      = await svc.GetAllAsync();

        Assert.Equal(3, eligible.Count);
        Assert.Equal(3, all.Count);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static PendingOrderService BuildV1Service(
        SapReplitAPI.Services.Neon.NeonDbContext db) =>
        new(db, Microsoft.Extensions.Logging.Abstractions.NullLogger<PendingOrderService>.Instance);

    private static CreateOrderDto MakeDto(string cardCode = "CUST-001") => new()
    {
        CardCode = cardCode,
        DocDate  = DateTime.UtcNow.Date,
        DocCur   = "TZS",
        Lines    =
        [
            new OrderLineDto { ItemCode = "ITEM-A", Quantity = 2, Price = 500m },
            new OrderLineDto { ItemCode = "ITEM-B", Quantity = 1, Price = 300m }
        ]
    };
}
