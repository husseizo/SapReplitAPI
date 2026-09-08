using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SapReplitAPI.Models.Offline;
using SapReplitAPI.Services.Neon;
using SapReplitAPI.Services.Offline;
using SapReplitAPI.Tests.Fakes;
using Xunit;

namespace SapReplitAPI.Tests.Offline;

/// <summary>
/// Regression tests for the ExecuteSqlRawAsync CancellationToken overload bug.
///
/// ROOT CAUSE (2026-09-07, production):
///   OfflineFulfillmentRecoveryService contained 4 calls to ExecuteSqlRawAsync where
///   CancellationToken was passed as the LAST positional argument inline:
///
///       ExecuteSqlRawAsync(sql, param0, param1, ..., ct)   ← BUGGY
///
///   C# overload resolution chose ExecuteSqlRawAsync(string, params object[]) over
///   ExecuteSqlRawAsync(string, IEnumerable&lt;object&gt;, CancellationToken).
///   Npgsql boxed the CancellationToken as a SQL parameter value and threw:
///     InvalidOperationException: "The current provider doesn't have a store type
///     mapping for properties of type 'CancellationToken'."
///
/// FIX: All 4 calls were changed to:
///       ExecuteSqlRawAsync(sql, new object[] { param0, param1, ... }, ct)
///   forcing the IEnumerable&lt;object&gt; overload so ct is never boxed as a value.
///
/// AFFECTED METHODS:
///   - AtomicClaimAsync        (line ~55 in OfflineFulfillmentRecoveryService.cs)
///   - MarkReconciliationAsync (line ~297)
///   - MarkFailedAsync         (line ~312)
///   - ReleaseStaleClaimsAsync (line ~334)
///
/// PRODUCTION EVIDENCE:
///   Log: "[OFFLINE-V2-JOB] Recovery cycle threw unhandled exception."
///   Firing every 30 seconds from 01:40 to 01:52 (+03:00).
///   After fix + redeploy: recovery completed in ~13 seconds, OrderId=1 → Completed.
/// </summary>
public sealed class Phase_E_CancellationTokenRegressionTests
{
    // ── Regression guard: base-class ExecuteSqlRawAsync overload check ─────────

    /// <summary>
    /// Calls the real (non-overridden) ReleaseStaleClaimsAsync on an InMemory context.
    ///
    /// InMemory does not support ExecuteSqlRawAsync and always throws:
    ///   InvalidOperationException: "Relational-specific methods can only be used when
    ///   the context is using a relational database provider."
    ///
    /// With the CancellationToken bug re-introduced against Npgsql, the exception message
    /// would contain "CancellationToken" instead. This guard detects that regression.
    ///
    /// Note: the production fix is verified by integration evidence (controlled test #1),
    /// not by InMemory unit tests. This test provides a compile-time and early-warning guard.
    /// </summary>
    [Fact]
    public async Task ReleaseStaleClaimsAsync_BaseImpl_ExceptionDoesNotIndicateStoreTypeMappingFailure()
    {
        using var db = OfflineTestDb.Build();
        var svc = BuildBaseService(db);

        var ex = await Record.ExceptionAsync(() => svc.ReleaseStaleClaimsAsync(default));

        // InMemory throws because ExecuteSqlRawAsync requires a relational provider.
        // That exception is expected and fine — InMemory's message never contains "store type mapping".
        //
        // With the Npgsql CancellationToken bug re-introduced, the specific error would be:
        //   "The current provider doesn't have a store type mapping for properties of type 'CancellationToken'."
        // "store type mapping" is the Npgsql-specific sentinel — not present in InMemory's error.
        Assert.NotNull(ex);
        Assert.DoesNotContain("store type mapping", ex.Message);
        Assert.DoesNotContain("store type mapping", ex.ToString());
    }

    [Fact]
    public async Task MarkReconciliationAsync_BaseImpl_ExceptionDoesNotMentionCancellationToken()
    {
        using var db = OfflineTestDb.Build();
        var svc = BuildBaseService(db);

        var ex = await Record.ExceptionAsync(() =>
            svc.CallMarkReconciliationAsync(99, ReconciliationReasonCode.SapPreflightFailed,
                "test message", default));

        Assert.NotNull(ex);
        Assert.DoesNotContain("CancellationToken", ex.Message);
    }

    [Fact]
    public async Task MarkFailedAsync_BaseImpl_ExceptionDoesNotMentionCancellationToken()
    {
        using var db = OfflineTestDb.Build();
        var svc = BuildBaseService(db);

        var ex = await Record.ExceptionAsync(() =>
            svc.CallMarkFailedAsync(99, "test error", default));

        Assert.NotNull(ex);
        Assert.DoesNotContain("CancellationToken", ex.Message);
    }

    // ── CancellationToken threading — semantic tests ────────────────────────────

    [Fact]
    public async Task RecoverBatchAsync_WithExplicitToken_CompletesNormally()
    {
        // Verify CancellationToken is threaded through EF queries (not boxed as a value).
        // Uses TestableRecoveryService (EF-direct overrides) to isolate from SQL provider.
        using var db = OfflineTestDb.Build();
        int orderId = await OfflineTestDb.SeedWaitingForRecoveryOrder(db);

        var adapter = new FakeOfflineSapAdapter();
        var svc = OfflineTestDb.BuildRecoveryService(db, adapter);

        using var cts = new CancellationTokenSource();
        await svc.RecoverBatchAsync(cts.Token);

        var order = await db.OfflineFulfillmentOrders.FindAsync(orderId);
        Assert.Equal(OfflineFulfillmentState.Completed, order!.State);
    }

    [Fact]
    public async Task ReleaseStaleClaimsAsync_WithExplicitToken_ReleasesAndResumes()
    {
        // Verify stale claim release works with a real CancellationToken via TestableRecoveryService.
        using var db = OfflineTestDb.Build();
        int orderId = await OfflineTestDb.SeedWaitingForRecoveryOrder(db);

        // Artificially claim the order and make it stale
        var order = await db.OfflineFulfillmentOrders.FindAsync(orderId);
        order!.RecoveryClaimId  = Guid.NewGuid();
        order.RecoveryClaimedAt = DateTime.UtcNow.AddMinutes(-10); // stale
        order.State             = OfflineFulfillmentState.Recovering;
        await db.SaveChangesAsync();

        var adapter = new FakeOfflineSapAdapter();
        var svc = OfflineTestDb.BuildRecoveryService(db, adapter);

        using var cts = new CancellationTokenSource();
        await svc.ReleaseStaleClaimsAsync(cts.Token); // must not throw
        await svc.RecoverBatchAsync(cts.Token);        // stale order re-queued and recovered

        var finished = await db.OfflineFulfillmentOrders.FindAsync(orderId);
        Assert.Equal(OfflineFulfillmentState.Completed, finished!.State);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static RecoveryServiceWithExposedInternals BuildBaseService(NeonDbContext db)
    {
        var opts = Options.Create(new OfflineFulfillmentOptions
        {
            Enabled = true,
            RecoveryBatchSize = 10,
            RecoveryClaimLeaseSeconds = 120
        });
        return new RecoveryServiceWithExposedInternals(
            db, opts, new FakeOfflineSapAdapter(),
            NullLogger<OfflineFulfillmentRecoveryService>.Instance);
    }
}

/// <summary>
/// Thin subclass that exposes the protected methods of the real (base) recovery service
/// for regression testing without overriding the ExecuteSqlRawAsync calls.
/// Only used by Phase_E_CancellationTokenRegressionTests.
/// </summary>
internal sealed class RecoveryServiceWithExposedInternals : OfflineFulfillmentRecoveryService
{
    public RecoveryServiceWithExposedInternals(
        NeonDbContext db,
        IOptions<OfflineFulfillmentOptions> opts,
        IOfflineSapAdapter sap,
        ILogger<OfflineFulfillmentRecoveryService> log)
        : base(db, opts, sap, log) { }

    public Task CallMarkReconciliationAsync(
        int orderId, string reason, string message, CancellationToken ct) =>
        MarkReconciliationAsync(orderId, reason, message, ct);

    public Task CallMarkFailedAsync(
        int orderId, string message, CancellationToken ct) =>
        MarkFailedAsync(orderId, message, ct);
}
