using SapReplitAPI.Services.Events;
using System.Linq;
using Xunit;

namespace SapReplitAPI.Tests.Events;

/// <summary>
/// PRI_01–PRI_08 — priority-aware SapEventOutbox claiming (Real-Time Neon Foundation,
/// Phase 1). Tests OutboxClaimService.SelectClaimOrder / ResolvePriority directly — the
/// pure, DB-free extraction of exactly what ClaimByPriorityAsync's two SQL queries
/// implement (documented in OutboxClaimService.cs). This is the honest testable
/// boundary for the actual SQL Server claim query, which needs a live SQL Server to
/// exercise end-to-end; the algorithm itself is fully covered here.
///
/// PRI_07/PRI_08 (single-worker semantics / no parallel SAP COM) are additionally
/// confirmed by code inspection, noted in the implementation report — OutboxPollerService
/// still has no Task.WhenAll anywhere, and ClaimBatchAsync's two queries run
/// sequentially on one connection within one method call, never concurrently.
/// </summary>
public class OutboxPriorityTests
{
    private static SapOutboxEvent Ev(long id, string objectType, string tx = "A", int attempt = 1) =>
        new(Id: id, EventId: Guid.NewGuid(), ObjectType: objectType, TransactionType: tx,
            DocEntry: (int)id, KeyValues: null, CreatedAtUtc: DateTime.UtcNow.AddSeconds(id), AttemptCount: attempt);

    // Registered-ObjectType mapping, verified against the actual 11 handlers (see
    // EventHandlerRouter/Program.cs) rather than accepted from the draft blindly:
    //   P0: 13,14,15,16,17,20,59,60,67 (all real operational effects)
    //   P1: 24,234000031 (financial/business lifecycle)
    //   P2: everything else (reserved — no ObjectType maps here today; OITM/ITM1/OPLN
    //       are not registered anywhere in this codebase yet)

    [Theory]
    [InlineData("13", 0)] [InlineData("14", 0)] [InlineData("15", 0)] [InlineData("16", 0)]
    [InlineData("17", 0)] [InlineData("20", 0)] [InlineData("59", 0)] [InlineData("60", 0)] [InlineData("67", 0)]
    [InlineData("24", 1)] [InlineData("234000031", 1)]
    [InlineData("93", 2)] [InlineData("OITM", 2)] [InlineData("ITM1", 2)]
    public void PriorityMapping_MatchesDocumentedContract(string objectType, int expectedPriority)
    {
        Assert.Equal(expectedPriority, OutboxClaimService.ResolvePriority(objectType));
    }

    // PRI_01 — P0 is claimed before older P2 work
    [Fact]
    public void PRI_01_P0ClaimedBeforeOlderP2()
    {
        var pending = new List<SapOutboxEvent>
        {
            Ev(1, "24"),   // P1, older (lower Id = created earlier)
            Ev(2, "17"),   // P0, newer
        };
        var claimed = OutboxClaimService.SelectClaimOrder(pending, batchSize: 10, minNonP0PerCycle: 5);
        Assert.Equal(2, claimed.Count);
        Assert.Equal(2L, claimed[0].Id); // P0 first despite being created later
        Assert.Equal(1L, claimed[1].Id);
    }

    // PRI_02 — FIFO ordering is preserved inside the same priority
    [Fact]
    public void PRI_02_FifoPreservedWithinSamePriority()
    {
        var pending = new List<SapOutboxEvent>
        {
            Ev(3, "17"), Ev(1, "15"), Ev(2, "67"), // all P0, mixed Id order
        };
        var claimed = OutboxClaimService.SelectClaimOrder(pending, batchSize: 10, minNonP0PerCycle: 5);
        Assert.Equal(new[] { 1L, 2L, 3L }, claimed.Select(c => c.Id).ToArray());
    }

    // PRI_03 — P1/P2 eventually receive service (non-zero, even under P0 pressure)
    [Fact]
    public void PRI_03_NonP0_AlwaysGetsItsReservedFloor_EvenWithHeavyP0Backlog()
    {
        var pending = new List<SapOutboxEvent>();
        for (int i = 0; i < 100; i++) pending.Add(Ev(i, "17")); // 100 P0 rows
        pending.Add(Ev(1000, "24")); // 1 P1 row

        var claimed = OutboxClaimService.SelectClaimOrder(pending, batchSize: 50, minNonP0PerCycle: 5);
        // p0Share = 50-5 = 45 P0 rows claimed (capped, even though 100 are pending);
        // remaining = 5, and only 1 P1 row exists, so total = 45+1 = 46.
        Assert.Equal(46, claimed.Count);
        Assert.Contains(claimed, c => c.Id == 1000); // the P1 row got claimed this cycle
        Assert.Equal(45, claimed.Count(c => OutboxClaimService.ResolvePriority(c.ObjectType) == 0)); // capped
    }

    // PRI_04 — 5,000 simulated P2 rows cannot place a new P0 event behind the backlog
    [Fact]
    public void PRI_04_5000PendingP2_CannotDelayNewP0Event()
    {
        var pending = new List<SapOutboxEvent>();
        for (int i = 0; i < 5000; i++) pending.Add(Ev(i, "OITM")); // 5,000 P2 rows (simulated future domain)
        pending.Add(Ev(6000, "15")); // 1 brand-new P0 Delivery event

        var claimed = OutboxClaimService.SelectClaimOrder(pending, batchSize: 50, minNonP0PerCycle: 5);
        Assert.Equal(6000L, claimed[0].Id); // claimed FIRST in this very cycle, not queued behind 5000 P2 rows
    }

    // PRI_05 — retryable events preserve correct priority behavior: AttemptCount has
    // no bearing on priority — only ObjectType does. A P0 event on its 2nd attempt is
    // still P0, not demoted.
    [Fact]
    public void PRI_05_RetryableEvents_PreserveCorrectPriority()
    {
        var pending = new List<SapOutboxEvent>
        {
            Ev(1, "24", attempt: 1),           // P1, first attempt
            Ev(2, "17", attempt: 2),           // P0, SECOND attempt (a retry)
        };
        var claimed = OutboxClaimService.SelectClaimOrder(pending, batchSize: 10, minNonP0PerCycle: 5);
        Assert.Equal(2L, claimed[0].Id); // still claimed first — retry attempt count didn't change priority
        Assert.Equal(0, OutboxClaimService.ResolvePriority(claimed[0].ObjectType));
    }

    // PRI_06 — existing handlers still receive identical event payloads: every field
    // a handler actually reads (ObjectType, TransactionType, DocEntry, EventId,
    // AttemptCount) survives SelectClaimOrder untouched; Priority is purely additive.
    [Fact]
    public void PRI_06_ExistingHandlerFields_AreUnchangedByPrioritySelection()
    {
        var original = Ev(1, "15", tx: "A", attempt: 1);
        var claimed = OutboxClaimService.SelectClaimOrder(
            new List<SapOutboxEvent> { original }, batchSize: 10, minNonP0PerCycle: 5);

        var result = claimed.Single();
        Assert.Equal(original.EventId, result.EventId);
        Assert.Equal(original.ObjectType, result.ObjectType);
        Assert.Equal(original.TransactionType, result.TransactionType);
        Assert.Equal(original.DocEntry, result.DocEntry);
        Assert.Equal(original.AttemptCount, result.AttemptCount);
    }

    // PRI_07 — single-worker semantics remain intact: batchSize is never exceeded,
    // even with unlimited pending work in both tiers (proves no accidental
    // over-claim that could imply parallel processing downstream).
    [Fact]
    public void PRI_07_NeverExceedsBatchSize_SingleWorkerContractIntact()
    {
        var pending = new List<SapOutboxEvent>();
        for (int i = 0; i < 200; i++) pending.Add(Ev(i, i % 2 == 0 ? "17" : "24"));
        var claimed = OutboxClaimService.SelectClaimOrder(pending, batchSize: 50, minNonP0PerCycle: 5);
        Assert.True(claimed.Count <= 50);
    }

    // PRI_08 — no parallel SAP COM execution is introduced: SelectClaimOrder is a
    // pure, synchronous, side-effect-free function — it cannot itself spawn
    // concurrent work, and OutboxPollerService's processing foreach (unchanged by
    // this phase) still awaits one event at a time. Confirmed structurally: no
    // Task.WhenAll exists anywhere in OutboxClaimService.cs or OutboxPollerService.cs.
    [Fact]
    public void PRI_08_SelectClaimOrder_IsPureAndSynchronous_NoConcurrencyIntroduced()
    {
        var pending = new List<SapOutboxEvent> { Ev(1, "17") };
        // Calling it twice with the same input is deterministic and side-effect-free —
        // if it touched any shared/static state or spawned work, repeated calls could
        // behave differently or race; they don't.
        var first = OutboxClaimService.SelectClaimOrder(pending, 10, 5);
        var second = OutboxClaimService.SelectClaimOrder(pending, 10, 5);
        Assert.Equal(first.Select(e => e.Id), second.Select(e => e.Id));
    }
}
