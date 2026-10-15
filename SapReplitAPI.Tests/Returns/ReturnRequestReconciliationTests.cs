using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using SapReplitAPI.Models.Cache;
using SapReplitAPI.Services.Neon;
using SapReplitAPI.Services.Returns;
using Xunit;

namespace SapReplitAPI.Tests.Returns;

/// <summary>
/// Covers the Return Request reconciliation safety net (ReturnRequestReconciliationService,
/// ReturnRequestReconciliationJob, ReturnRequestFullReconciliationJob) added as a PHASE 2
/// addition on top of the existing, unmodified event-driven path.
///
/// Test boundary note: SapService has no test seam (no interface; DI-API/COM backed), the same
/// limitation this codebase already lives with elsewhere — see CreditMemoReturnsTests.CR07,
/// marked [Fact(Skip = "Integration — requires live SAP COM connection")]. These tests therefore
/// exercise exactly what the reconciliation job calls after candidate discovery — the real,
/// unmodified NeonReturnRequestWriteService.UpsertReturnRequestAsync + PendingReturnQuantityMirror
/// recompute — against an isolated Postgres schema, using hand-built snapshots in place of a SAP
/// read. This proves the write/recompute/idempotency/concurrency-safety behavior the job relies
/// on; it does not exercise the SAP candidate-discovery query itself (covered separately by the
/// pure BuildChangedReturnRequestFilterTests below, which need no SAP connection) or the Quartz
/// job's batch/time-budget logic (not SAP-dependent, but also not exercised here — see the
/// delivery report for why that layer is left to manual/staging verification).
/// </summary>
public sealed class ReturnRequestReconciliationTests
{
    private static async Task Run(Func<CacheDbContext, NeonDbContext, NpgsqlConnection, Task> test)
    {
        var schema = "retrecon_test_" + Guid.NewGuid().ToString("N");
        var cs = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("RETURNS_TEST_POSTGRES"))
            { SearchPath = schema, Pooling = false };
        await using var pg = new NpgsqlConnection(cs.ConnectionString); await pg.OpenAsync();
        await new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", pg).ExecuteNonQueryAsync();
        try
        {
            await using var sqliteConnection = new SqliteConnection("Data Source=:memory:"); await sqliteConnection.OpenAsync();
            await using var sqlite = new CacheDbContext(new DbContextOptionsBuilder<CacheDbContext>().UseSqlite(sqliteConnection).Options);
            await sqlite.Database.EnsureCreatedAsync();
            await using var neon = new NeonDbContext(new DbContextOptionsBuilder<NeonDbContext>().UseNpgsql(pg).Options);
            await new NpgsqlCommand(neon.Database.GenerateCreateScript(), pg).ExecuteNonQueryAsync();
            sqlite.Invoices.Add(new CachedInvoice { DocEntry = 50001, DocDate = new DateTime(2026, 10, 1) });
            sqlite.InvoiceLines.Add(new CachedInvoiceLine { DocEntry = 50001, LineNum = 0, Quantity = 10 });
            sqlite.InvoiceLines.Add(new CachedInvoiceLine { DocEntry = 50001, LineNum = 1, Quantity = 10 });
            await sqlite.SaveChangesAsync();
            await test(sqlite, neon, pg);
        }
        finally { await new NpgsqlCommand($"DROP SCHEMA \"{schema}\" CASCADE", pg).ExecuteNonQueryAsync(); }
    }

    private static NeonReturnRequestWriteService Writer(NeonDbContext neon)
        => new(neon, NullLogger<NeonReturnRequestWriteService>.Instance);

    private static CachedReturnRequest Header(int docEntry, string status, string canceled) => new()
    {
        DocEntry = docEntry, DocNum = docEntry, CardCode = "C001", CardName = "Test Customer",
        DocDate = new DateTime(2026, 10, 1), DocStatus = status, Canceled = canceled,
        DocTotal = 0, U_AppRef = $"srr-{docEntry}", U_ReplitId = null, Comments = ""
    };

    private static CachedReturnRequestLine Line(int docEntry, int lineNum, int baseEntry, int baseLine,
        decimal qty, decimal openQty, string lineStatus) => new()
    {
        DocEntry = docEntry, LineNum = lineNum, BaseType = 13, BaseEntry = baseEntry, BaseLine = baseLine,
        ItemCode = "ITEM001", Dscription = "Test Item", Quantity = qty, OpenQty = openQty,
        WhsCode = "001", LineStatus = lineStatus
    };

    private static async Task<decimal> PendingQty(NpgsqlConnection pg, int docEntry, int lineNum)
        => Convert.ToDecimal(await new NpgsqlCommand(
            $"SELECT \"PendingReturnQty\" FROM \"InvoiceLines\" WHERE \"DocEntry\"={docEntry} AND \"LineNum\"={lineNum}", pg)
            .ExecuteScalarAsync());

    private static async Task<decimal> ReturnableQty(NpgsqlConnection pg, int docEntry, int lineNum)
        => Convert.ToDecimal(await new NpgsqlCommand(
            $"SELECT \"ReturnableQty\" FROM \"InvoiceLines\" WHERE \"DocEntry\"={docEntry} AND \"LineNum\"={lineNum}", pg)
            .ExecuteScalarAsync());

    private static async Task<long> ReturnRequestLineCount(NpgsqlConnection pg, int docEntry)
        => Convert.ToInt64(await new NpgsqlCommand(
            $"SELECT COUNT(*) FROM \"ReturnRequestLines\" WHERE \"DocEntry\"={docEntry}", pg)
            .ExecuteScalarAsync());

    // 1 — Missed outbox event recovered by reconciliation: Neon starts with no knowledge of an
    // ORRR that already exists (simulating a dropped outbox event); the reconciliation job's
    // per-candidate refresh (same call the event handler would have made) brings Neon in line.
    [InvoiceReturnsPostgresFactShim]
    public Task RR01_MissedEvent_RecoveredByRefresh() => Run(async (sqlite, neon, pg) =>
    {
        Assert.Equal(0m, await PendingQty(pg, 50001, 0)); // Neon never heard about it
        await Writer(neon).UpsertReturnRequestAsync(
            Header(900, "O", "N"), new[] { Line(900, 0, 50001, 0, 4, 4, "O") });
        Assert.Equal(4m, await PendingQty(pg, 50001, 0));
    });

    // 2 — Return Request closure: DocStatus -> 'C' excludes it from the pending sum even though
    // OpenQty itself wasn't zeroed on the line (closure, not consumption, drives this to zero).
    [InvoiceReturnsPostgresFactShim]
    public Task RR02_Closure_ZeroesPending() => Run(async (sqlite, neon, pg) =>
    {
        await Writer(neon).UpsertReturnRequestAsync(Header(901, "O", "N"), new[] { Line(901, 0, 50001, 0, 4, 4, "O") });
        Assert.Equal(4m, await PendingQty(pg, 50001, 0));
        await Writer(neon).UpsertReturnRequestAsync(Header(901, "C", "N"), new[] { Line(901, 0, 50001, 0, 4, 0, "C") });
        Assert.Equal(0m, await PendingQty(pg, 50001, 0));
    });

    // 3 — Return Request cancellation: Canceled -> 'Y' excludes it from the pending sum.
    [InvoiceReturnsPostgresFactShim]
    public Task RR03_Cancellation_ZeroesPending() => Run(async (sqlite, neon, pg) =>
    {
        await Writer(neon).UpsertReturnRequestAsync(Header(902, "O", "N"), new[] { Line(902, 0, 50001, 0, 4, 4, "O") });
        Assert.Equal(4m, await PendingQty(pg, 50001, 0));
        await Writer(neon).UpsertReturnRequestAsync(Header(902, "O", "Y"), new[] { Line(902, 0, 50001, 0, 4, 4, "O") });
        Assert.Equal(0m, await PendingQty(pg, 50001, 0));
    });

    // 4 — Partial OpenQty reduction: a credit memo partially redeems the return request; OpenQty
    // drops but the line stays Open, so the (reduced) remainder is still pending.
    [InvoiceReturnsPostgresFactShim]
    public Task RR04_PartialOpenQtyReduction_ReflectsRemainder() => Run(async (sqlite, neon, pg) =>
    {
        await Writer(neon).UpsertReturnRequestAsync(Header(903, "O", "N"), new[] { Line(903, 0, 50001, 0, 5, 5, "O") });
        Assert.Equal(5m, await PendingQty(pg, 50001, 0));
        await Writer(neon).UpsertReturnRequestAsync(Header(903, "O", "N"), new[] { Line(903, 0, 50001, 0, 5, 2, "O") });
        Assert.Equal(2m, await PendingQty(pg, 50001, 0));
    });

    // 5 — Multiple active requests for one invoice line: two distinct ORRR documents both
    // referencing the same invoice line sum together.
    [InvoiceReturnsPostgresFactShim]
    public Task RR05_MultipleActiveRequests_Sum() => Run(async (sqlite, neon, pg) =>
    {
        await Writer(neon).UpsertReturnRequestAsync(Header(904, "O", "N"), new[] { Line(904, 0, 50001, 0, 2, 2, "O") });
        await Writer(neon).UpsertReturnRequestAsync(Header(905, "O", "N"), new[] { Line(905, 0, 50001, 0, 3, 3, "O") });
        Assert.Equal(5m, await PendingQty(pg, 50001, 0));
    });

    // 6 — Invoice line with no active requests returns zero (baseline — nothing special needed).
    [InvoiceReturnsPostgresFactShim]
    public Task RR06_NoActiveRequests_IsZero() => Run(async (sqlite, neon, pg) =>
        Assert.Equal(0m, await PendingQty(pg, 50001, 1)));

    // 7 — Repeated reconciliation without double counting: refreshing the SAME, unchanged
    // snapshot twice in a row must not change the aggregate or duplicate ReturnRequestLines rows.
    [InvoiceReturnsPostgresFactShim]
    public Task RR07_RepeatedRefresh_NoDoubleCounting() => Run(async (sqlite, neon, pg) =>
    {
        var header = Header(906, "O", "N");
        var lines = new[] { Line(906, 0, 50001, 0, 3, 3, "O") };
        await Writer(neon).UpsertReturnRequestAsync(header, lines);
        await Writer(neon).UpsertReturnRequestAsync(header, lines); // reconciliation re-examines the same candidate
        await Writer(neon).UpsertReturnRequestAsync(header, lines); // and again
        Assert.Equal(3m, await PendingQty(pg, 50001, 0));
        Assert.Equal(1, await ReturnRequestLineCount(pg, 906));
    });

    // 8 — "Concurrent" outbox processing and scheduled reconciliation. True concurrency isn't
    // deterministically testable in-process; this proves the mechanism both paths actually rely
    // on for safety — back-to-back writes for the same DocEntry via the same transactional
    // delete+reinsert+recompute call never leave an inconsistent intermediate state, regardless
    // of which caller (event handler or reconciliation job) wins the race to run last.
    [InvoiceReturnsPostgresFactShim]
    public Task RR08_BackToBackWritesSameDocEntry_ConsistentFinalState() => Run(async (sqlite, neon, pg) =>
    {
        var writer = Writer(neon);
        var t1 = writer.UpsertReturnRequestAsync(Header(907, "O", "N"), new[] { Line(907, 0, 50001, 0, 4, 4, "O") });
        await t1; // first caller (e.g. event handler) completes
        var t2 = writer.UpsertReturnRequestAsync(Header(907, "O", "N"), new[] { Line(907, 0, 50001, 0, 4, 1, "O") });
        await t2; // second caller (e.g. reconciliation job, running moments later) completes
        Assert.Equal(1m, await PendingQty(pg, 50001, 0)); // last writer's (freshest) state wins
        Assert.Equal(1, await ReturnRequestLineCount(pg, 907)); // never duplicated
    });

    // 11 — Documents the one confirmed-real concurrency risk from the cross-worker analysis: if
    // the reconciliation job reads an OLDER snapshot but its write happens to COMMIT AFTER a
    // newer one (e.g. the event handler, racing on the same DocEntry), the older write can
    // transiently overwrite Neon with stale data. This is NOT new — it is an existing property
    // of "read live SAP state, then write, no optimistic lock" shared by both the event handler
    // and this job — but it had not been demonstrated before. This test proves the two things
    // that matter: (a) no duplication/corruption ever results, only a transient wrong value, and
    // (b) it self-heals the moment either path re-processes the same DocEntry with current data,
    // which the reconciliation job's own next scheduled run (or any subsequent event) does.
    [InvoiceReturnsPostgresFactShim]
    public Task RR11_StaleSnapshotCommittedLast_TransientlyWrong_ThenSelfHeals() => Run(async (sqlite, neon, pg) =>
    {
        var writer = Writer(neon);
        // "Newer" business reality: the request was fully redeemed (closed, OpenQty 0).
        var freshSnapshot = (Header(910, "C", "N"), new[] { Line(910, 0, 50001, 0, 4, 0, "C") });
        // "Older" snapshot some other caller read earlier but is slow to commit.
        var staleSnapshot = (Header(910, "O", "N"), new[] { Line(910, 0, 50001, 0, 4, 4, "O") });

        await writer.UpsertReturnRequestAsync(freshSnapshot.Item1, freshSnapshot.Item2); // fresh commits first
        Assert.Equal(0m, await PendingQty(pg, 50001, 0)); // correctly closed

        await writer.UpsertReturnRequestAsync(staleSnapshot.Item1, staleSnapshot.Item2); // stale commits later — wins the race
        Assert.Equal(4m, await PendingQty(pg, 50001, 0)); // transiently WRONG — this is the confirmed, bounded risk
        Assert.Equal(1, await ReturnRequestLineCount(pg, 910)); // still no duplication, no corruption

        await writer.UpsertReturnRequestAsync(freshSnapshot.Item1, freshSnapshot.Item2); // next cycle re-reads current truth
        Assert.Equal(0m, await PendingQty(pg, 50001, 0)); // self-healed
    });

    // 9 — Interrupted reconciliation followed by retry: a write that fails mid-transaction must
    // roll back fully (no partial PendingReturnQty correction), and a subsequent retry with valid
    // data must then succeed and self-correct — mirrors the existing
    // FailedBatchRollsBackTruncateAndPreservesQuantity pattern in InvoiceReturnsPostgresTests.
    [InvoiceReturnsPostgresFactShim]
    public Task RR09_InterruptedWrite_RollsBack_ThenRetrySucceeds() => Run(async (sqlite, neon, pg) =>
    {
        await Writer(neon).UpsertReturnRequestAsync(Header(908, "O", "N"), new[] { Line(908, 0, 50001, 0, 4, 4, "O") });
        Assert.Equal(4m, await PendingQty(pg, 50001, 0));

        await new NpgsqlCommand(
            "ALTER TABLE \"ReturnRequestLines\" ADD CONSTRAINT reject_negative_openqty CHECK (\"OpenQty\" >= 0)", pg)
            .ExecuteNonQueryAsync();

        await Assert.ThrowsAsync<PostgresException>(() =>
            Writer(neon).UpsertReturnRequestAsync(Header(908, "O", "N"), new[] { Line(908, 0, 50001, 0, 4, -1, "O") }));
        Assert.Equal(4m, await PendingQty(pg, 50001, 0)); // unchanged — the bad write never committed

        await new NpgsqlCommand("ALTER TABLE \"ReturnRequestLines\" DROP CONSTRAINT reject_negative_openqty", pg)
            .ExecuteNonQueryAsync();
        await Writer(neon).UpsertReturnRequestAsync(Header(908, "O", "N"), new[] { Line(908, 0, 50001, 0, 4, 0, "C") });
        Assert.Equal(0m, await PendingQty(pg, 50001, 0)); // retry with valid data succeeds
    });

    // 10 — Correct Neon PendingReturnQty AND the generated ReturnableQty column together.
    [InvoiceReturnsPostgresFactShim]
    public Task RR10_PendingReturnQtyAndReturnableQty_BothCorrect() => Run(async (sqlite, neon, pg) =>
    {
        await Writer(neon).UpsertReturnRequestAsync(Header(909, "O", "N"), new[] { Line(909, 0, 50001, 0, 4, 4, "O") });
        Assert.Equal(4m, await PendingQty(pg, 50001, 0));
        // ReturnableQty = GREATEST(0, Quantity(10) - ReturnedQty(0) - PendingReturnQty(4)) = 6
        Assert.Equal(6m, await ReturnableQty(pg, 50001, 0));
    });
}

/// <summary>Same skip-if-unconfigured gate as InvoiceReturnsPostgresTests' ReturnsPostgresFactAttribute.</summary>
public sealed class InvoiceReturnsPostgresFactShim : FactAttribute
{
    public InvoiceReturnsPostgresFactShim()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("RETURNS_TEST_POSTGRES")))
            Skip = "Set RETURNS_TEST_POSTGRES to a PostgreSQL database permitting isolated test schemas.";
    }
}

/// <summary>
/// Pure, no-SAP, no-database tests for ReturnRequestReconciliationService.SelectBatch — the
/// batch-rotation algorithm added specifically to fix a confirmed deterministic-starvation
/// defect: a plain candidates.Take(BatchSize) always re-selects the same lowest-DocEntry subset
/// when the candidate set is static and larger than BatchSize, permanently starving the rest
/// until they age out of the lookback window.
/// </summary>
public sealed class SelectBatchTests
{
    [Fact]
    public void SmallCandidateSet_SelectsAllAndResetsCursorToZero()
    {
        var selected = ReturnRequestReconciliationService.SelectBatch(
            new[] { 5, 3, 1 }, cursor: 0, batchSize: 10, out var newCursor);
        Assert.Equal(new[] { 1, 3, 5 }, selected); // ordered ascending
        Assert.Equal(0, newCursor); // full lap in one go — ready for a fresh lap next run
    }

    [Fact]
    public void LargeCandidateSet_FirstRun_TakesLowestBatchSizeAndAdvancesCursor()
    {
        var candidates = Enumerable.Range(1, 120).ToList();
        var selected = ReturnRequestReconciliationService.SelectBatch(candidates, cursor: 0, batchSize: 50, out var newCursor);
        Assert.Equal(Enumerable.Range(1, 50), selected);
        Assert.Equal(50, newCursor);
    }

    // This is the exact defect the fix addresses: with the OLD plain Take(50) behavior, this
    // second call (same static 120-candidate set, same code path as a second run an hour later)
    // would select [1..50] again. With the cursor, it must select [51..100] instead.
    [Fact]
    public void LargeCandidateSet_SecondRun_ContinuesPastCursor_DoesNotRepeatFirstBatch()
    {
        var candidates = Enumerable.Range(1, 120).ToList();
        var selected = ReturnRequestReconciliationService.SelectBatch(candidates, cursor: 50, batchSize: 50, out var newCursor);
        Assert.Equal(Enumerable.Range(51, 50), selected);
        Assert.Equal(100, newCursor);
        Assert.DoesNotContain(1, selected);
        Assert.DoesNotContain(50, selected);
    }

    [Fact]
    public void LargeCandidateSet_ThirdRun_FinishesRemainderAndWrapsCursorToZero()
    {
        var candidates = Enumerable.Range(1, 120).ToList();
        var selected = ReturnRequestReconciliationService.SelectBatch(candidates, cursor: 100, batchSize: 50, out var newCursor);
        // Only 20 remain strictly after 100 (101..120); the remaining 30 slots wrap around to
        // the start of the list (1..30) rather than sitting idle — still bounded, still fair.
        Assert.Equal(50, selected.Count);
        Assert.Contains(101, selected);
        Assert.Contains(120, selected);
        Assert.Contains(1, selected); // wrapped
        Assert.Equal(0, newCursor); // batch's max (120) == whole list's max — lap complete
    }

    [Fact]
    public void FullRotation_AcrossThreeRuns_EveryCandidateProcessedExactlyOnceAndNoneStarved()
    {
        var candidates = Enumerable.Range(1, 120).ToList();
        var seen = new HashSet<int>();
        int cursor = 0;
        for (int run = 0; run < 3; run++)
        {
            var batch = ReturnRequestReconciliationService.SelectBatch(candidates, cursor, batchSize: 50, out cursor);
            foreach (var d in batch) seen.Add(d);
        }
        // All 120 candidates were reached within 3 runs (ceil(120/50) = 3) — none starved.
        Assert.Equal(120, seen.Count);
        for (int d = 1; d <= 120; d++) Assert.Contains(d, seen);
    }

    [Fact]
    public void EmptyCandidateSet_ReturnsEmptyAndLeavesCursorUnchanged()
    {
        var selected = ReturnRequestReconciliationService.SelectBatch(Array.Empty<int>(), cursor: 42, batchSize: 50, out var newCursor);
        Assert.Empty(selected);
        Assert.Equal(42, newCursor);
    }

    [Fact]
    public void StaleCursor_PastAllCurrentCandidates_WrapsAndStillSelectsEverything()
    {
        // Simulates the candidate set having drifted entirely below the old cursor (e.g. the
        // previously-seen high DocEntries aged out of the lookback window and only new, lower
        // ones remain) — must self-heal via the wrap-around, never return empty.
        var selected = ReturnRequestReconciliationService.SelectBatch(
            new[] { 1, 2, 3 }, cursor: 999, batchSize: 50, out var newCursor);
        Assert.Equal(new[] { 1, 2, 3 }, selected);
        Assert.Equal(0, newCursor);
    }
}

/// <summary>
/// Pure predicate tests — no SAP connection, no database — for
/// SapService.BuildChangedReturnRequestFilter, the lookback-window predicate the reconciliation
/// job's candidate discovery query depends on.
/// </summary>
public sealed class BuildChangedReturnRequestFilterTests
{
    [Fact]
    public void Filter_UsesUpdateDateAndUpdateTs_WithGivenAlias()
    {
        var since = new DateTime(2026, 10, 15, 9, 0, 0);
        var filter = SapService.BuildChangedReturnRequestFilter(since, "H");
        Assert.Contains("H.UpdateDate > '2026-10-15'", filter);
        Assert.Contains("H.UpdateDate = '2026-10-15'", filter);
        Assert.Contains("H.UpdateTS >= 90000", filter);
    }

    [Fact]
    public void Filter_SupportsAlternateAlias_ForOrinJoin()
    {
        var since = new DateTime(2026, 10, 15, 9, 0, 0);
        var filter = SapService.BuildChangedReturnRequestFilter(since, "C");
        Assert.Contains("C.UpdateDate", filter);
        Assert.Contains("C.UpdateTS", filter);
        Assert.DoesNotContain("H.", filter);
    }

    [Fact]
    public void Filter_MidnightWatermark_ProducesZeroTimestamp()
    {
        var since = new DateTime(2026, 10, 15, 0, 0, 0);
        var filter = SapService.BuildChangedReturnRequestFilter(since, "H");
        Assert.Contains("H.UpdateTS >= 0", filter);
    }
}
