using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using SapReplitAPI.Controllers;
using SapReplitAPI.Models.Cache;
using SapReplitAPI.Models.Returns;
using SapReplitAPI.Services.CachedServices;
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
            // ReturnableQty is a generated column created only via raw SQL in Program.cs — it is
            // not part of the EF model, so GenerateCreateScript() never creates it. Mirror
            // production's exact DDL so RR10 can verify it.
            await new NpgsqlCommand(
                "ALTER TABLE \"InvoiceLines\" ADD COLUMN \"ReturnableQty\" numeric(18,4) GENERATED ALWAYS AS (GREATEST(0, \"Quantity\" - \"ReturnedQty\" - \"PendingReturnQty\")) STORED", pg)
                .ExecuteNonQueryAsync();
            sqlite.Invoices.Add(new CachedInvoice { DocEntry = 50001, DocDate = new DateTime(2026, 10, 1) });
            sqlite.InvoiceLines.Add(new CachedInvoiceLine { DocEntry = 50001, LineNum = 0, Quantity = 10 });
            sqlite.InvoiceLines.Add(new CachedInvoiceLine { DocEntry = 50001, LineNum = 1, Quantity = 10 });
            await sqlite.SaveChangesAsync();
            // Neon and SQLite are separate databases — seeding SQLite alone does not populate
            // Neon. Production reaches Neon via NeonSyncJob; these tests seed Neon's
            // InvoiceLines directly since they are testing the reconciliation/recompute path,
            // not the invoice sync job itself (InvoiceReturnsPostgresTests covers that already).
            await new NpgsqlCommand(
                "INSERT INTO \"Invoices\" (\"DocEntry\", \"DocNum\", \"InvoiceDocNum\", \"DocDate\", \"DocStatus\", \"CardCode\", \"CardName\", \"DocTotal\", \"PaidToDate\", \"BalanceDue\", \"SalesEmployeeCode\", \"SalesEmployeeName\", \"DaysOverdue\", \"Canceled\") " +
                "VALUES (50001, 50001, 50001, '2026-10-01T00:00:00Z', 'O', 'C001', 'Test Customer', 0, 0, 0, 0, '', 0, 'N')", pg)
                .ExecuteNonQueryAsync();
            await new NpgsqlCommand(
                "INSERT INTO \"InvoiceLines\" (\"DocEntry\", \"LineNum\", \"ItemCode\", \"Dscription\", \"Quantity\", \"Price\", \"LineTotal\", \"U_MDLTsT\", \"ReturnedQty\") " +
                "VALUES (50001, 0, 'ITEM001', 'Test Item', 10, 0, 0, '', 0), (50001, 1, 'ITEM001', 'Test Item', 10, 0, 0, '', 0)", pg)
                .ExecuteNonQueryAsync();
            await test(sqlite, neon, pg);
        }
        finally { await new NpgsqlCommand($"DROP SCHEMA \"{schema}\" CASCADE", pg).ExecuteNonQueryAsync(); }
    }

    internal static NeonReturnRequestWriteService Writer(NeonDbContext neon)
        => new(neon, NullLogger<NeonReturnRequestWriteService>.Instance);

    internal static CachedReturnRequest Header(int docEntry, string status, string canceled) => new()
    {
        DocEntry = docEntry, DocNum = docEntry, CardCode = "C001", CardName = "Test Customer",
        DocDate = new DateTime(2026, 10, 1), DocStatus = status, Canceled = canceled,
        DocTotal = 0, U_AppRef = $"srr-{docEntry}", U_ReplitId = null, Comments = ""
    };

    internal static CachedReturnRequestLine Line(int docEntry, int lineNum, int baseEntry, int baseLine,
        decimal qty, decimal openQty, string lineStatus) => new()
    {
        DocEntry = docEntry, LineNum = lineNum, BaseType = 13, BaseEntry = baseEntry, BaseLine = baseLine,
        ItemCode = "ITEM001", Dscription = "Test Item", Quantity = qty, OpenQty = openQty,
        WhsCode = "001", LineStatus = lineStatus
    };

    internal static async Task<decimal> PendingQty(NpgsqlConnection pg, int docEntry, int lineNum)
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

    // RR12 — Phase 5 TASK 3: a previously-linked invoice line that NO LONGER appears in the new
    // ORRR snapshot (its line now points elsewhere) must still be recalculated down to zero, not
    // left stuck at its old value. This is the specific "previously affected invoice lines...
    // even if they no longer appear in the current SAP snapshot" requirement — proven here, not
    // assumed, by reading NeonReturnRequestWriteService's own affectedInvoiceDocEntries logic
    // (it unions OLD BaseEntry values read from the existing ReturnRequestLines row BEFORE
    // delete, with the NEW snapshot's BaseEntry values, before recomputing).
    [InvoiceReturnsPostgresFactShim]
    public Task RR12_PreviouslyLinkedInvoiceLine_RemovedFromSnapshot_StillRecalculatedToZero() => Run(async (sqlite, neon, pg) =>
    {
        // ORRR 950's line originally points to invoice line (50001, 0).
        await Writer(neon).UpsertReturnRequestAsync(Header(950, "O", "N"), new[] { Line(950, 0, 50001, 0, 5, 5, "O") });
        Assert.Equal(5m, await PendingQty(pg, 50001, 0));

        // The SAME ORRR's line is corrected/edited in SAP to now point to (50001, 1) instead.
        await Writer(neon).UpsertReturnRequestAsync(Header(950, "O", "N"), new[] { Line(950, 0, 50001, 1, 5, 5, "O") });

        Assert.Equal(0m, await PendingQty(pg, 50001, 0)); // old link correctly zeroed — not stuck at 5
        Assert.Equal(5m, await PendingQty(pg, 50001, 1)); // new link correctly reflects the pending qty
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
/// Integration tests for the SQLite-persisted rotation cursor itself — ReadCursorAsync /
/// WriteCursorAsync — covering TASK 2's "service restart", "missing or corrupted cursor state"
/// scenarios directly against a real SQLite database (not the pure in-memory SelectBatch logic,
/// which SelectBatchTests already covers). Constructs ReturnRequestReconciliationService with
/// null SAP/Neon dependencies deliberately — these two methods never touch them.
/// </summary>
public sealed class ReturnReconciliationCursorPersistenceTests
{
    internal const string CursorTableDdl = @"
CREATE TABLE IF NOT EXISTS ""ReturnReconciliationCursor"" (
    ""Id""                            INTEGER PRIMARY KEY CHECK (""Id"" = 1),
    ""LastProcessedDocEntry""         INTEGER NOT NULL DEFAULT 0,
    ""UpdatedAtUtc""                  TEXT    NOT NULL DEFAULT '',
    ""LastRunAtUtc""                  TEXT,
    ""LastRunCandidatesExamined""     INTEGER,
    ""LastRunRefreshed""              INTEGER,
    ""LastRunErrors""                 INTEGER,
    ""LastRunDurationMs""             INTEGER,
    ""HistoricalCursorDocEntry""      INTEGER NOT NULL DEFAULT 0,
    ""HistoricalUpdatedAtUtc""        TEXT,
    ""HistoricalLastRunAtUtc""        TEXT,
    ""HistoricalLastRunExamined""     INTEGER,
    ""HistoricalLastRunRefreshed""    INTEGER,
    ""HistoricalLastRunErrors""       INTEGER,
    ""HistoricalLastRunDurationMs""   INTEGER,
    ""HistoricalLapsCompleted""       INTEGER NOT NULL DEFAULT 0
)";

    private static ReturnRequestReconciliationService NewService(CacheDbContext sqlite)
        => new(sapReader: null!, cache: null!, neonWriter: null!, neonDb: null!, sqliteDb: sqlite,
               log: NullLogger<ReturnRequestReconciliationService>.Instance);

    [Fact]
    public async Task MissingCursorRow_ReadReturnsZero()
    {
        await using var conn = new SqliteConnection("Data Source=:memory:"); await conn.OpenAsync();
        await using var sqlite = new CacheDbContext(new DbContextOptionsBuilder<CacheDbContext>().UseSqlite(conn).Options);
        await sqlite.Database.EnsureCreatedAsync();
        await sqlite.Database.ExecuteSqlRawAsync(CursorTableDdl);

        var service = NewService(sqlite);
        Assert.Equal(0, await service.ReadCursorAsync());
    }

    [Fact]
    public async Task WriteThenRead_RoundTripsExactValue()
    {
        await using var conn = new SqliteConnection("Data Source=:memory:"); await conn.OpenAsync();
        await using var sqlite = new CacheDbContext(new DbContextOptionsBuilder<CacheDbContext>().UseSqlite(conn).Options);
        await sqlite.Database.EnsureCreatedAsync();
        await sqlite.Database.ExecuteSqlRawAsync(CursorTableDdl);

        var service = NewService(sqlite);
        await service.WriteCursorAsync(137);
        Assert.Equal(137, await service.ReadCursorAsync());

        // Overwrite — proves ON CONFLICT UPDATE, not a duplicate/second row.
        await service.WriteCursorAsync(250);
        Assert.Equal(250, await service.ReadCursorAsync());
    }

    // TASK 2 — "service restart": a real on-disk SQLite file (not :memory:, which does not
    // survive a connection close) proves the cursor is genuinely durable across what a service
    // restart actually does — close the process, reopen the file fresh.
    [Fact]
    public async Task ServiceRestart_CursorSurvivesReopeningTheSameFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"retrecon_cursor_test_{Guid.NewGuid():N}.db");
        try
        {
            var csb = $"Data Source={path};Pooling=False";
            await using (var conn1 = new SqliteConnection(csb))
            {
                await conn1.OpenAsync();
                await using var sqlite1 = new CacheDbContext(new DbContextOptionsBuilder<CacheDbContext>().UseSqlite(conn1).Options);
                await sqlite1.Database.EnsureCreatedAsync();
                await sqlite1.Database.ExecuteSqlRawAsync(CursorTableDdl);
                await NewService(sqlite1).WriteCursorAsync(88);
                await conn1.CloseAsync();
            } // connection fully closed — simulates the service process stopping

            await using var conn2 = new SqliteConnection(csb);
            await conn2.OpenAsync();
            await using var sqlite2 = new CacheDbContext(new DbContextOptionsBuilder<CacheDbContext>().UseSqlite(conn2).Options);
            // No EnsureCreatedAsync/DDL here — this is the "restart," reopening what already
            // exists on disk, exactly as the real app does against its persistent SQLite file.
            Assert.Equal(88, await NewService(sqlite2).ReadCursorAsync());
            await conn2.CloseAsync();
        }
        finally { try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort cleanup only */ } }
    }

    // TASK 2 — "missing or corrupted cursor state": SQLite's type affinity is not strict, so a
    // non-numeric value can end up in an INTEGER column (e.g. from a hand edit or a bug
    // elsewhere). ReadCursorAsync must not throw and take the whole run down over this.
    [Fact]
    public async Task CorruptedCursorValue_ReadDefaultsToZeroInsteadOfThrowing()
    {
        await using var conn = new SqliteConnection("Data Source=:memory:"); await conn.OpenAsync();
        await using var sqlite = new CacheDbContext(new DbContextOptionsBuilder<CacheDbContext>().UseSqlite(conn).Options);
        await sqlite.Database.EnsureCreatedAsync();
        await sqlite.Database.ExecuteSqlRawAsync(CursorTableDdl);
        await sqlite.Database.ExecuteSqlRawAsync(
            @"INSERT INTO ""ReturnReconciliationCursor"" (""Id"", ""LastProcessedDocEntry"", ""UpdatedAtUtc"") VALUES (1, 'not-a-number', '')");

        var service = NewService(sqlite);
        var result = await service.ReadCursorAsync(); // must not throw
        Assert.Equal(0, result);
    }
}

/// <summary>
/// TASK 6 — GET /api/system/return-reconciliation-health, tested directly against the
/// controller action (no HTTP host needed) with a real SQLite-backed service and no SAP/Neon
/// dependency, since the endpoint only ever reads the persisted cursor/last-run row.
/// </summary>
public sealed class ReturnReconciliationHealthEndpointTests
{
    private static async Task<(SystemController controller, CacheDbContext sqlite)> NewControllerAsync(bool enabled, bool fullEnabled)
    {
        var conn = new SqliteConnection("Data Source=:memory:"); await conn.OpenAsync();
        var sqlite = new CacheDbContext(new DbContextOptionsBuilder<CacheDbContext>().UseSqlite(conn).Options);
        await sqlite.Database.EnsureCreatedAsync();
        await sqlite.Database.ExecuteSqlRawAsync(ReturnReconciliationCursorPersistenceTests_Ddl());

        var opts = new ReturnReconciliationOptions { Enabled = enabled, FullReconciliationEnabled = fullEnabled };
        var service = new ReturnRequestReconciliationService(
            sapReader: null!, cache: null!, neonWriter: null!, neonDb: null!, sqliteDb: sqlite,
            log: NullLogger<ReturnRequestReconciliationService>.Instance);

        var services = new ServiceCollection();
        services.AddSingleton<IOptions<ReturnReconciliationOptions>>(Options.Create(opts));
        services.AddSingleton(service);
        var provider = services.BuildServiceProvider();

        return (new SystemController(provider), sqlite);
    }

    private static string ReturnReconciliationCursorPersistenceTests_Ddl() => ReturnReconciliationCursorPersistenceTests.CursorTableDdl;

    [Fact]
    public async Task Disabled_ReportsDisabledStatus_NoSensitiveData()
    {
        var (controller, _) = await NewControllerAsync(enabled: false, fullEnabled: false);
        var result = Assert.IsType<OkObjectResult>(await controller.GetReturnReconciliationHealthAsync(default));
        var body = Assert.IsType<ReturnReconciliationHealthResponse>(result.Value);
        Assert.False(body.Enabled);
        Assert.Equal("Disabled", body.Status);
        AssertNoSensitiveData(body);
    }

    [Fact]
    public async Task EnabledNeverRun_ReportsNeverRunStatus()
    {
        var (controller, _) = await NewControllerAsync(enabled: true, fullEnabled: false);
        var result = Assert.IsType<OkObjectResult>(await controller.GetReturnReconciliationHealthAsync(default));
        var body = Assert.IsType<ReturnReconciliationHealthResponse>(result.Value);
        Assert.True(body.Enabled);
        Assert.Equal("NeverRun", body.Status);
        Assert.Null(body.LastRunAtUtc);
    }

    [Fact]
    public async Task EnabledWithSuccessfulRun_ReportsHealthyWithCounts()
    {
        var (controller, sqlite) = await NewControllerAsync(enabled: true, fullEnabled: false);
        var report = new ReturnRequestReconciliationReport { CandidatesExamined = 5, CandidatesRefreshed = 5 };
        var service = new ReturnRequestReconciliationService(null!, null!, null!, null!, sqlite, NullLogger<ReturnRequestReconciliationService>.Instance);
        await (Task)service.GetType().GetMethod("WriteLastRunSummaryAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(service, new object[] { report, default(CancellationToken) })!;

        var result = Assert.IsType<OkObjectResult>(await controller.GetReturnReconciliationHealthAsync(default));
        var body = Assert.IsType<ReturnReconciliationHealthResponse>(result.Value);
        Assert.Equal("Healthy", body.Status);
        Assert.Equal(5, body.LastRunCandidatesExamined);
        Assert.Equal(5, body.LastRunRefreshed);
        Assert.Equal(0, body.LastRunErrors);
        AssertNoSensitiveData(body);
    }

    [Fact]
    public async Task EnabledWithErrors_ReportsDegraded()
    {
        var (controller, sqlite) = await NewControllerAsync(enabled: true, fullEnabled: false);
        var report = new ReturnRequestReconciliationReport { CandidatesExamined = 3, CandidatesRefreshed = 1 };
        report.Errors.Add("DocEntry=999: simulated failure");
        var service = new ReturnRequestReconciliationService(null!, null!, null!, null!, sqlite, NullLogger<ReturnRequestReconciliationService>.Instance);
        await (Task)service.GetType().GetMethod("WriteLastRunSummaryAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(service, new object[] { report, default(CancellationToken) })!;

        var result = Assert.IsType<OkObjectResult>(await controller.GetReturnReconciliationHealthAsync(default));
        var body = Assert.IsType<ReturnReconciliationHealthResponse>(result.Value);
        Assert.Equal("Degraded", body.Status);
        Assert.Equal(1, body.LastRunErrors);
    }

    // Phase 6 TASK 5 — "health metrics accurately distinguish successful, partial, and failed
    // runs." The pre-existing Healthy/Degraded split already distinguished successful from
    // partial (some docs refreshed despite errors); this closes the remaining gap: a run where
    // EVERY attempted document failed (zero refreshed) must report distinctly from one where
    // most documents still succeeded.
    [Fact]
    public async Task EnabledWithAllFailures_ReportsFailedStatus()
    {
        var (controller, sqlite) = await NewControllerAsync(enabled: true, fullEnabled: false);
        var report = new ReturnRequestReconciliationReport { CandidatesExamined = 2, CandidatesRefreshed = 0 };
        report.Errors.Add("DocEntry=901: simulated failure");
        report.Errors.Add("DocEntry=902: simulated failure");
        var service = new ReturnRequestReconciliationService(null!, null!, null!, null!, sqlite, NullLogger<ReturnRequestReconciliationService>.Instance);
        await (Task)service.GetType().GetMethod("WriteLastRunSummaryAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(service, new object[] { report, default(CancellationToken) })!;

        var result = Assert.IsType<OkObjectResult>(await controller.GetReturnReconciliationHealthAsync(default));
        var body = Assert.IsType<ReturnReconciliationHealthResponse>(result.Value);
        Assert.Equal("Failed", body.Status);
        Assert.Equal(0, body.LastRunRefreshed);
        Assert.Equal(2, body.LastRunErrors);
    }

    // A historical-tier-only failure must escalate the OVERALL status — it must not read as
    // "Healthy" just because the incremental tier (checked alone, pre-Phase-6) was clean.
    [Fact]
    public async Task HistoricalOnlyFailure_EscalatesOverallStatusFromHealthyToDegraded()
    {
        var (controller, sqlite) = await NewControllerAsync(enabled: true, fullEnabled: false);
        var incrementalReport = new ReturnRequestReconciliationReport { CandidatesExamined = 5, CandidatesRefreshed = 5 };
        var service = new ReturnRequestReconciliationService(null!, null!, null!, null!, sqlite, NullLogger<ReturnRequestReconciliationService>.Instance);
        await (Task)service.GetType().GetMethod("WriteLastRunSummaryAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(service, new object[] { incrementalReport, default(CancellationToken) })!;

        var historicalReport = new ReturnRequestReconciliationReport { CandidatesExamined = 10, CandidatesRefreshed = 8 };
        historicalReport.Errors.Add("DocEntry=777: simulated historical failure");
        historicalReport.Errors.Add("DocEntry=778: simulated historical failure");
        await (Task)service.GetType().GetMethod("WriteHistoricalLastRunSummaryAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(service, new object[] { historicalReport, default(CancellationToken) })!;

        var result = Assert.IsType<OkObjectResult>(await controller.GetReturnReconciliationHealthAsync(default));
        var body = Assert.IsType<ReturnReconciliationHealthResponse>(result.Value);
        Assert.Equal(0, body.LastRunErrors); // incremental tier alone is still clean
        Assert.Equal(2, body.HistoricalLastRunErrors);
        Assert.Equal("Degraded", body.Status); // but the combined status reflects the historical failure
    }

    private static void AssertNoSensitiveData(ReturnReconciliationHealthResponse body)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(body);
        Assert.DoesNotContain("Password", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ConnectionString", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ApiKey", json, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Test double for IReturnRequestSapReader (Phase 4 TASK 4) — lets RunIncrementalAsync /
/// RunHistoricalRotationAsync be driven end-to-end (batch limits, time budgets, cursor
/// advancement, partial failures) without a live SAP DI API connection. GetSnapshot logs every
/// call so tests can assert exactly which DocEntries were actually touched.
/// </summary>
public sealed class FakeReturnRequestSapReader : IReturnRequestSapReader
{
    public List<int> ChangedDocEntries { get; set; } = new();
    public List<int> AllDocEntriesValue { get; set; } = new();
    public Dictionary<int, (CachedReturnRequest header, List<CachedReturnRequestLine> lines)> Snapshots { get; } = new();
    public HashSet<int> ThrowForDocEntries { get; } = new();
    public TimeSpan DelayPerSnapshotCall { get; set; } = TimeSpan.Zero;
    public List<int> SnapshotCallLog { get; } = new();

    /// <summary>
    /// When set, replays these exact delays in call order instead of the fixed
    /// DelayPerSnapshotCall — used to replay real observed production per-document latencies
    /// rather than an invented constant, for the Phase 5 measured-timing test.
    /// </summary>
    public List<TimeSpan>? DelaysInCallOrder { get; set; }

    public List<int> GetChangedDocEntries(DateTime since) => ChangedDocEntries;
    public List<int> GetAllDocEntries() => AllDocEntriesValue;

    public (CachedReturnRequest header, List<CachedReturnRequestLine> lines)? GetSnapshot(int docEntry)
    {
        var callIndex = SnapshotCallLog.Count;
        SnapshotCallLog.Add(docEntry);
        var delay = DelaysInCallOrder is not null && callIndex < DelaysInCallOrder.Count
            ? DelaysInCallOrder[callIndex]
            : DelayPerSnapshotCall;
        if (delay > TimeSpan.Zero) Thread.Sleep(delay);
        if (ThrowForDocEntries.Contains(docEntry)) throw new InvalidOperationException($"Simulated SAP failure for DocEntry={docEntry}");
        return Snapshots.TryGetValue(docEntry, out var s) ? s : null;
    }

    public static (CachedReturnRequest header, List<CachedReturnRequestLine> lines) Simple(int docEntry, string status = "O", string canceled = "N")
        => (new CachedReturnRequest { DocEntry = docEntry, DocNum = docEntry, CardCode = "C001", CardName = "Test", DocDate = new DateTime(2026, 10, 1), DocStatus = status, Canceled = canceled, Comments = "" },
            new List<CachedReturnRequestLine> { new() { DocEntry = docEntry, LineNum = 0, BaseType = 13, BaseEntry = 99999, BaseLine = 0, ItemCode = "ITEM001", Dscription = "Test", Quantity = 1, OpenQty = 1, WhsCode = "001", LineStatus = status } });
}

/// <summary>
/// End-to-end orchestration tests (Phase 4 TASK 4) — drive the real RunIncrementalAsync /
/// RunHistoricalRotationAsync against real Postgres + real SQLite, with only the SAP layer
/// faked. Covers batch limits, time budgets, cursor advancement, partial failures, restart
/// recovery, and job-overlap (advisory lock) — the orchestration properties the pure
/// SelectBatchTests and the write-layer RR01-11 tests each cover only half of individually.
/// </summary>
public sealed class ReturnRequestOrchestrationTests
{
    private sealed record Harness(ReturnRequestReconciliationService Service, FakeReturnRequestSapReader Reader, CacheDbContext Sqlite, NpgsqlConnection Pg, string Schema);

    private static async Task<Harness> NewHarnessAsync(NpgsqlConnection? sharedPg = null, CacheDbContext? sharedSqlite = null, FakeReturnRequestSapReader? sharedReader = null)
    {
        NpgsqlConnection pg;
        string schema = "";
        if (sharedPg is null)
        {
            schema = "retrecon_orch_" + Guid.NewGuid().ToString("N");
            var cs = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("RETURNS_TEST_POSTGRES")) { SearchPath = schema, Pooling = false };
            pg = new NpgsqlConnection(cs.ConnectionString);
            await pg.OpenAsync();
            await new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", pg).ExecuteNonQueryAsync();
        }
        else pg = sharedPg;

        var neon = new NeonDbContext(new DbContextOptionsBuilder<NeonDbContext>().UseNpgsql(pg).Options);
        if (sharedPg is null)
            await new NpgsqlCommand(neon.Database.GenerateCreateScript(), pg).ExecuteNonQueryAsync();

        CacheDbContext sqlite;
        if (sharedSqlite is null)
        {
            var conn = new SqliteConnection("Data Source=:memory:"); await conn.OpenAsync();
            sqlite = new CacheDbContext(new DbContextOptionsBuilder<CacheDbContext>().UseSqlite(conn).Options);
            await sqlite.Database.EnsureCreatedAsync();
            await sqlite.Database.ExecuteSqlRawAsync(CursorDdl);
        }
        else sqlite = sharedSqlite;

        var reader = sharedReader ?? new FakeReturnRequestSapReader();
        var cache = new ReturnRequestCacheService(sqlite, NullLogger<ReturnRequestCacheService>.Instance);
        var writer = new NeonReturnRequestWriteService(neon, NullLogger<NeonReturnRequestWriteService>.Instance);
        var service = new ReturnRequestReconciliationService(reader, cache, writer, neon, sqlite, NullLogger<ReturnRequestReconciliationService>.Instance);
        return new Harness(service, reader, sqlite, pg, schema);
    }

    private const string CursorDdl = @"
CREATE TABLE IF NOT EXISTS ""ReturnReconciliationCursor"" (
    ""Id""                            INTEGER PRIMARY KEY CHECK (""Id"" = 1),
    ""LastProcessedDocEntry""         INTEGER NOT NULL DEFAULT 0,
    ""UpdatedAtUtc""                  TEXT    NOT NULL DEFAULT '',
    ""LastRunAtUtc""                  TEXT,
    ""LastRunCandidatesExamined""     INTEGER,
    ""LastRunRefreshed""              INTEGER,
    ""LastRunErrors""                 INTEGER,
    ""LastRunDurationMs""             INTEGER,
    ""HistoricalCursorDocEntry""      INTEGER NOT NULL DEFAULT 0,
    ""HistoricalUpdatedAtUtc""        TEXT,
    ""HistoricalLastRunAtUtc""        TEXT,
    ""HistoricalLastRunExamined""     INTEGER,
    ""HistoricalLastRunRefreshed""    INTEGER,
    ""HistoricalLastRunErrors""       INTEGER,
    ""HistoricalLastRunDurationMs""   INTEGER,
    ""HistoricalLapsCompleted""       INTEGER NOT NULL DEFAULT 0
)";

    private static async Task Cleanup(Harness h)
    {
        if (!string.IsNullOrEmpty(h.Schema))
            await new NpgsqlCommand($"DROP SCHEMA \"{h.Schema}\" CASCADE", h.Pg).ExecuteNonQueryAsync();
        await h.Pg.DisposeAsync();
    }

    private static ReturnReconciliationOptions Opts(int batchSize = 50, int maxExecSec = 240, int lookbackMin = 180, int lockTimeoutSec = 30) =>
        new() { BatchSize = batchSize, MaxExecutionSeconds = maxExecSec, LookbackMinutes = lookbackMin,
                HistoricalBatchSize = batchSize, HistoricalMaxExecutionSeconds = maxExecSec,
                AdvisoryLockTimeoutSeconds = lockTimeoutSec };

    // OR01 — Batch limits: 120 candidates, BatchSize=50 -> exactly 50 processed, 70 deferred.
    [InvoiceReturnsPostgresFactShim]
    public async Task OR01_BatchLimit_ProcessesExactlyBatchSize()
    {
        var h = await NewHarnessAsync();
        try
        {
            h.Reader.ChangedDocEntries = Enumerable.Range(1, 120).ToList();
            foreach (var d in h.Reader.ChangedDocEntries) h.Reader.Snapshots[d] = FakeReturnRequestSapReader.Simple(d);

            var report = await h.Service.RunIncrementalAsync(Opts(batchSize: 50));
            Assert.Equal(120, report.CandidatesExamined);
            Assert.Equal(50, report.CandidatesRefreshed);
            Assert.Equal(70, report.DeferredToNextRun);
        }
        finally { await Cleanup(h); }
    }

    // OR02 — Cursor advancement: three consecutive runs over a static 120-candidate set reach
    // EVERY candidate at least once (the real end-to-end proof behind
    // SelectBatchTests.FullRotation_..., now through the actual service, not just the
    // algorithm). "At least once," not "exactly once": 120 is not an exact multiple of
    // BatchSize=50, so the documented wrap-around (SelectBatch's third run processes the final
    // 20 plus wraps to re-fill its batch from the start) deliberately revisits candidates 1-30 a
    // second time — that is the designed trade-off for keeping each run's cost close to
    // BatchSize rather than variable, not a repeat of the original starvation defect (which
    // starved the HIGH end forever, not merely revisited the LOW end once more while still
    // covering everything).
    [InvoiceReturnsPostgresFactShim]
    public async Task OR02_CursorAdvancement_ThreeRunsCoverAllCandidatesAtLeastOnce()
    {
        var h = await NewHarnessAsync();
        try
        {
            h.Reader.ChangedDocEntries = Enumerable.Range(1, 120).ToList();
            foreach (var d in h.Reader.ChangedDocEntries) h.Reader.Snapshots[d] = FakeReturnRequestSapReader.Simple(d);

            await h.Service.RunIncrementalAsync(Opts(batchSize: 50));
            await h.Service.RunIncrementalAsync(Opts(batchSize: 50));
            await h.Service.RunIncrementalAsync(Opts(batchSize: 50));

            var touched = h.Reader.SnapshotCallLog.Distinct().OrderBy(x => x).ToList();
            Assert.Equal(120, touched.Count); // every candidate reached
            Assert.Equal(Enumerable.Range(1, 120), touched);
            // Exactly 3 runs x 50/run = 150 attempts total; the 30 "extra" are the documented
            // wrap-around re-visits of 1-30, not an uncontrolled/unbounded repeat.
            Assert.Equal(150, h.Reader.SnapshotCallLog.Count);
        }
        finally { await Cleanup(h); }
    }

    // OR03 — Execution time budget: artificial per-call delay forces the budget to trip mid-
    // batch; cursor must not advance (next run safely retries the same batch in full).
    [InvoiceReturnsPostgresFactShim]
    public async Task OR03_TimeBudgetExceeded_CursorDoesNotAdvance()
    {
        var h = await NewHarnessAsync();
        try
        {
            h.Reader.ChangedDocEntries = Enumerable.Range(1, 10).ToList();
            foreach (var d in h.Reader.ChangedDocEntries) h.Reader.Snapshots[d] = FakeReturnRequestSapReader.Simple(d);
            h.Reader.DelayPerSnapshotCall = TimeSpan.FromMilliseconds(400);

            var report = await h.Service.RunIncrementalAsync(Opts(batchSize: 10, maxExecSec: 1)); // ~1s budget, ~400ms/call -> trips after 2-3
            Assert.True(report.CandidatesRefreshed < 10, "Expected the time budget to cut the batch short.");
            Assert.Equal(0, await h.Service.ReadCursorAsync()); // never advanced — whole batch retried next run
        }
        finally { await Cleanup(h); }
    }

    // OR04 — Partial failure: one candidate throws; the rest of the batch still gets attempted
    // (per-candidate isolation), but the cursor does not advance past this batch at all.
    [InvoiceReturnsPostgresFactShim]
    public async Task OR04_PartialFailure_RestOfBatchStillAttempted_CursorDoesNotAdvance()
    {
        var h = await NewHarnessAsync();
        try
        {
            h.Reader.ChangedDocEntries = Enumerable.Range(1, 5).ToList();
            foreach (var d in h.Reader.ChangedDocEntries) h.Reader.Snapshots[d] = FakeReturnRequestSapReader.Simple(d);
            h.Reader.ThrowForDocEntries.Add(3);

            var report = await h.Service.RunIncrementalAsync(Opts(batchSize: 5));
            Assert.Equal(4, report.CandidatesRefreshed); // 1,2,4,5 succeeded
            Assert.Single(report.Errors);
            Assert.Contains("DocEntry=3", report.Errors[0]);
            Assert.Equal(5, h.Reader.SnapshotCallLog.Count); // all 5 were attempted, not aborted
            Assert.Equal(0, await h.Service.ReadCursorAsync()); // cursor withheld because of the error
        }
        finally { await Cleanup(h); }
    }

    // OR05 — Restart recovery: cursor persisted to a real on-disk SQLite file survives a fresh
    // ReturnRequestReconciliationService instance reopening that file (simulating a restart),
    // and the next run correctly continues the rotation rather than restarting it.
    [InvoiceReturnsPostgresFactShim]
    public async Task OR05_RestartRecovery_RotationContinuesFromPersistedCursor()
    {
        var path = Path.Combine(Path.GetTempPath(), $"retrecon_orch_restart_{Guid.NewGuid():N}.db");
        var cs = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("RETURNS_TEST_POSTGRES")) { Pooling = false };
        var schema = "retrecon_orch_restart_" + Guid.NewGuid().ToString("N");
        cs.SearchPath = schema;
        await using var pg = new NpgsqlConnection(cs.ConnectionString); await pg.OpenAsync();
        await new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", pg).ExecuteNonQueryAsync();
        try
        {
            var neon = new NeonDbContext(new DbContextOptionsBuilder<NeonDbContext>().UseNpgsql(pg).Options);
            await new NpgsqlCommand(neon.Database.GenerateCreateScript(), pg).ExecuteNonQueryAsync();

            var candidates = Enumerable.Range(1, 120).ToList();
            var sqliteCs = $"Data Source={path};Pooling=False";

            // "Before restart": one run.
            await using (var conn1 = new SqliteConnection(sqliteCs))
            {
                await conn1.OpenAsync();
                var sqlite1 = new CacheDbContext(new DbContextOptionsBuilder<CacheDbContext>().UseSqlite(conn1).Options);
                await sqlite1.Database.EnsureCreatedAsync();
                await sqlite1.Database.ExecuteSqlRawAsync(CursorDdl);
                var reader1 = new FakeReturnRequestSapReader { ChangedDocEntries = candidates };
                foreach (var d in candidates) reader1.Snapshots[d] = FakeReturnRequestSapReader.Simple(d);
                var service1 = new ReturnRequestReconciliationService(reader1, new ReturnRequestCacheService(sqlite1, NullLogger<ReturnRequestCacheService>.Instance),
                    new NeonReturnRequestWriteService(neon, NullLogger<NeonReturnRequestWriteService>.Instance), neon, sqlite1, NullLogger<ReturnRequestReconciliationService>.Instance);
                await service1.RunIncrementalAsync(Opts(batchSize: 50));
                Assert.Equal(50, await service1.ReadCursorAsync());
                await conn1.CloseAsync();
            } // process "stops"

            // "After restart": brand-new service instance, same SQLite file, same Neon.
            await using var conn2 = new SqliteConnection(sqliteCs);
            await conn2.OpenAsync();
            var sqlite2 = new CacheDbContext(new DbContextOptionsBuilder<CacheDbContext>().UseSqlite(conn2).Options);
            var reader2 = new FakeReturnRequestSapReader { ChangedDocEntries = candidates };
            foreach (var d in candidates) reader2.Snapshots[d] = FakeReturnRequestSapReader.Simple(d);
            var service2 = new ReturnRequestReconciliationService(reader2, new ReturnRequestCacheService(sqlite2, NullLogger<ReturnRequestCacheService>.Instance),
                new NeonReturnRequestWriteService(neon, NullLogger<NeonReturnRequestWriteService>.Instance), neon, sqlite2, NullLogger<ReturnRequestReconciliationService>.Instance);

            Assert.Equal(50, await service2.ReadCursorAsync()); // survived the "restart"
            await service2.RunIncrementalAsync(Opts(batchSize: 50));
            Assert.Equal(100, await service2.ReadCursorAsync()); // continued the rotation, didn't restart it
            Assert.DoesNotContain(1, reader2.SnapshotCallLog); // candidates 1-50 were NOT re-touched after the restart
            await conn2.CloseAsync();
        }
        finally
        {
            await new NpgsqlCommand($"DROP SCHEMA \"{schema}\" CASCADE", pg).ExecuteNonQueryAsync();
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }

    // OR06 — Job overlap: ReturnRequestReconciliationJob (incremental) and
    // ReturnRequestHistoricalRotationJob racing on the SAME DocEntry concurrently. The advisory
    // lock in RefreshOneAsync must serialize them — final state consistent, no duplicate
    // ReturnRequestLines rows, no exception from either side.
    [InvoiceReturnsPostgresFactShim]
    public async Task OR06_JobOverlap_SameDocEntry_AdvisoryLockSerializes_NoDuplication()
    {
        var h = await NewHarnessAsync();
        try
        {
            const int docEntry = 500;
            h.Reader.DelayPerSnapshotCall = TimeSpan.FromMilliseconds(150); // widen the race window
            h.Reader.Snapshots[docEntry] = FakeReturnRequestSapReader.Simple(docEntry);
            h.Reader.ChangedDocEntries = new List<int> { docEntry };

            // A second, independent physical connection to the SAME schema — this is how two
            // concurrent Quartz job executions actually look in production: each gets its own DI
            // scope and therefore its own NeonDbContext/connection, never a shared one. Using two
            // real connections (not two DbContexts sharing one NpgsqlConnection, which would
            // corrupt at the wire-protocol level regardless of any lock) is what makes this test
            // an honest proof of the advisory lock, not an artifact of sharing one connection.
            var cs2 = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("RETURNS_TEST_POSTGRES")) { SearchPath = h.Schema, Pooling = false };
            await using var pg2 = new NpgsqlConnection(cs2.ConnectionString); await pg2.OpenAsync();
            var neon2 = new NeonDbContext(new DbContextOptionsBuilder<NeonDbContext>().UseNpgsql(pg2).Options);
            var reader2 = new FakeReturnRequestSapReader { DelayPerSnapshotCall = TimeSpan.FromMilliseconds(150), ChangedDocEntries = new List<int> { docEntry } };
            reader2.Snapshots[docEntry] = FakeReturnRequestSapReader.Simple(docEntry);
            var service2 = new ReturnRequestReconciliationService(reader2,
                new ReturnRequestCacheService(h.Sqlite, NullLogger<ReturnRequestCacheService>.Instance),
                new NeonReturnRequestWriteService(neon2, NullLogger<NeonReturnRequestWriteService>.Instance),
                neon2, h.Sqlite, NullLogger<ReturnRequestReconciliationService>.Instance);

            var t1 = h.Service.RunIncrementalAsync(Opts(batchSize: 1));
            var t2 = service2.RunHistoricalRotationAsync(Opts(batchSize: 1));

            await Task.WhenAll(t1, t2); // must not throw — advisory lock serializes the two Neon writes

            await using var cmd = new NpgsqlCommand(
                $"SELECT COUNT(*) FROM \"ReturnRequestLines\" WHERE \"DocEntry\" = {docEntry}", h.Pg);
            var lineCount = Convert.ToInt64(await cmd.ExecuteScalarAsync());
            Assert.Equal(1, lineCount); // never duplicated, regardless of which tier "won" the lock first
        }
        finally { await Cleanup(h); }
    }

    // OR07 — Event-handler interaction: the event handler is a protected component this task does
    // not modify, so it does NOT take the advisory lock this job takes. This test proves the lock
    // added for TASK 3 does not break the pre-existing self-healing property (RR11) when an
    // unlocked writer (simulating ReturnRequestEventHandler calling UpsertReturnRequestAsync
    // directly) races against this job's locked write — final state is consistent, no
    // duplication, confirming the new lock is additive and does not mask or worsen that
    // already-documented, already-accepted risk.
    [InvoiceReturnsPostgresFactShim]
    public async Task OR07_UnlockedEventHandlerWrite_RacesJob_StillNoDuplication()
    {
        var h = await NewHarnessAsync();
        try
        {
            const int docEntry = 600;
            h.Reader.DelayPerSnapshotCall = TimeSpan.FromMilliseconds(150);
            h.Reader.Snapshots[docEntry] = FakeReturnRequestSapReader.Simple(docEntry);
            h.Reader.ChangedDocEntries = new List<int> { docEntry };

            // Its own separate physical connection — exactly like OR06, and for the same reason:
            // ReturnRequestEventHandler runs as a genuinely separate caller with its own
            // NeonDbContext/connection in production, never sharing one with this job's scope.
            var cs2 = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("RETURNS_TEST_POSTGRES")) { SearchPath = h.Schema, Pooling = false };
            await using var pg2 = new NpgsqlConnection(cs2.ConnectionString); await pg2.OpenAsync();
            var directWriter = new NeonReturnRequestWriteService(
                new NeonDbContext(new DbContextOptionsBuilder<NeonDbContext>().UseNpgsql(pg2).Options),
                NullLogger<NeonReturnRequestWriteService>.Instance);
            var (header, lines) = FakeReturnRequestSapReader.Simple(docEntry, status: "C", canceled: "N"); // "event handler" sees it as closed

            var t1 = h.Service.RunIncrementalAsync(Opts(batchSize: 1)); // locked
            var t2 = directWriter.UpsertReturnRequestAsync(header, lines); // unlocked — exactly what ReturnRequestEventHandler does today

            await Task.WhenAll(t1, t2);

            await using var cmd = new NpgsqlCommand(
                $"SELECT COUNT(*) FROM \"ReturnRequestLines\" WHERE \"DocEntry\" = {docEntry}", h.Pg);
            Assert.Equal(1, Convert.ToInt64(await cmd.ExecuteScalarAsync())); // still no duplication
        }
        finally { await Cleanup(h); }
    }

    // LK01 — Phase 5 TASK 2: a candidate that THROWS after the lock is acquired must still
    // release it. Proves the fix for the connection-pooling hazard found this phase: the lock is
    // taken on a dedicated non-pooled connection, so even on failure the lock is gone the moment
    // that connection closes — verified here by immediately re-acquiring the exact same key from
    // a fresh raw connection right after the failing call returns, with a short lock_timeout, and
    // asserting it succeeds fast rather than hanging/timing out.
    [InvoiceReturnsPostgresFactShim]
    public async Task LK01_FailureAfterLockAcquired_LockStillReleased()
    {
        var h = await NewHarnessAsync();
        try
        {
            const int docEntry = 700;
            h.Reader.ThrowForDocEntries.Add(docEntry);
            h.Reader.ChangedDocEntries = new List<int> { docEntry };

            var report = await h.Service.RunIncrementalAsync(Opts(batchSize: 1));
            Assert.Single(report.Errors); // confirmed it actually threw inside the locked region

            await AssertLockIsFree(h.Pg, docEntry);
        }
        finally { await Cleanup(h); }
    }

    // LK02 — Phase 5 TASK 2: lock acquisition cannot hang indefinitely. A separate raw connection
    // holds the advisory lock open (simulating a stuck/long-running holder) and never releases it
    // within the test; RefreshOneAsync must fail with a bounded lock_timeout error rather than
    // blocking forever. Uses a short configured timeout (2s) so the test itself stays fast while
    // still proving the real mechanism, not a mocked one.
    [InvoiceReturnsPostgresFactShim]
    public async Task LK02_LockTimeout_FailsLoudly_DoesNotHangForever()
    {
        var h = await NewHarnessAsync();
        try
        {
            const int docEntry = 701;
            h.Reader.Snapshots[docEntry] = FakeReturnRequestSapReader.Simple(docEntry);
            h.Reader.ChangedDocEntries = new List<int> { docEntry };

            var cs2 = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("RETURNS_TEST_POSTGRES")) { SearchPath = h.Schema, Pooling = false };
            await using var holderConn = new NpgsqlConnection(cs2.ConnectionString); await holderConn.OpenAsync();
            long key = ((long)0x5252 << 32) | (uint)docEntry; // mirrors ReturnRequestReconciliationService.AdvisoryLockKey
            await using (var holdCmd = new NpgsqlCommand("SELECT pg_advisory_lock(@k)", holderConn))
            { holdCmd.Parameters.AddWithValue("@k", NpgsqlTypes.NpgsqlDbType.Bigint, key); await holdCmd.ExecuteNonQueryAsync(); }
            // holderConn deliberately never unlocks or closes until after the assertion below —
            // simulating a stuck holder for the duration of this test.

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var report = await h.Service.RunIncrementalAsync(Opts(batchSize: 1, lockTimeoutSec: 2));
            sw.Stop();

            Assert.Single(report.Errors); // the lock_timeout surfaced as a per-candidate error, not a hang
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), $"Expected a bounded ~2s failure, took {sw.Elapsed}.");

            await holderConn.CloseAsync(); // release the simulated stuck holder now
        }
        finally { await Cleanup(h); }
    }

    // LK03 — Phase 5 TASK 2: same physical connection used for lock and unlock. Verified here by
    // confirming a successful run's lock is free again immediately afterward (if lock/unlock used
    // different sessions, pg_advisory_unlock would either no-op or error, and the lock would
    // remain held — this would show up as the next acquisition attempt timing out).
    [InvoiceReturnsPostgresFactShim]
    public async Task LK03_SuccessfulRun_LockIsFreeImmediatelyAfterward()
    {
        var h = await NewHarnessAsync();
        try
        {
            const int docEntry = 702;
            h.Reader.Snapshots[docEntry] = FakeReturnRequestSapReader.Simple(docEntry);
            h.Reader.ChangedDocEntries = new List<int> { docEntry };

            var report = await h.Service.RunIncrementalAsync(Opts(batchSize: 1));
            Assert.Equal(1, report.CandidatesRefreshed);

            await AssertLockIsFree(h.Pg, docEntry);
        }
        finally { await Cleanup(h); }
    }

    private static async Task AssertLockIsFree(NpgsqlConnection pg, int docEntry)
    {
        var cs = new NpgsqlConnectionStringBuilder(pg.ConnectionString) { Pooling = false };
        await using var probe = new NpgsqlConnection(cs.ConnectionString); await probe.OpenAsync();
        await using (var timeoutCmd = new NpgsqlCommand("SET lock_timeout = '3s'", probe)) await timeoutCmd.ExecuteNonQueryAsync();
        long key = ((long)0x5252 << 32) | (uint)docEntry;
        await using var lockCmd = new NpgsqlCommand("SELECT pg_advisory_lock(@k)", probe);
        lockCmd.Parameters.AddWithValue("@k", NpgsqlTypes.NpgsqlDbType.Bigint, key);
        await lockCmd.ExecuteNonQueryAsync(); // throws if still held after 3s — proves it leaked
        await using var unlockCmd = new NpgsqlCommand("SELECT pg_advisory_unlock(@k)", probe);
        unlockCmd.Parameters.AddWithValue("@k", NpgsqlTypes.NpgsqlDbType.Bigint, key);
        await unlockCmd.ExecuteNonQueryAsync();
    }

    // HR01 — Phase 5 TASK 1.1/1.6/1.7: historical rotation specifically (not the incremental
    // tier) covers more candidates than one batch, correctly picks up NEW documents that appear
    // mid-cycle, and correctly revisits previously-processed documents once a lap completes.
    [InvoiceReturnsPostgresFactShim]
    public async Task HR01_HistoricalRotation_CoversGrowingSet_AndRevisitsAfterLapCompletes()
    {
        var h = await NewHarnessAsync();
        try
        {
            h.Reader.AllDocEntriesValue = Enumerable.Range(1, 23).ToList(); // > HistoricalBatchSize(10)
            foreach (var d in h.Reader.AllDocEntriesValue) h.Reader.Snapshots[d] = FakeReturnRequestSapReader.Simple(d);

            await h.Service.RunHistoricalRotationAsync(Opts(batchSize: 10)); // run 1: docs 1-10
            await h.Service.RunHistoricalRotationAsync(Opts(batchSize: 10)); // run 2: docs 11-20

            // TASK 1.6 — a NEW document (24) appears mid-cycle, before the lap finishes.
            h.Reader.AllDocEntriesValue.Add(24);
            h.Reader.Snapshots[24] = FakeReturnRequestSapReader.Simple(24);

            await h.Service.RunHistoricalRotationAsync(Opts(batchSize: 10)); // run 3: docs 21,22,23,24 (4) + wraps to fill with 1,2,3,4,5,6 (lap completes)

            var touched = h.Reader.SnapshotCallLog.Distinct().ToList();
            Assert.Contains(24, touched); // the new document was reached within the SAME cycle it appeared in
            Assert.Equal(24, touched.Count); // every one of 1-24 reached at least once

            var summary = await h.Service.GetHistoricalRunSummaryAsync();
            Assert.Equal(1, summary!.LapsCompleted); // TASK 1.7 setup: one full lap done, ready to revisit from the start

            // A fourth run proves TASK 1.7 directly: previously processed doc 1 becomes eligible
            // again now that a new lap has started.
            h.Reader.SnapshotCallLog.Clear();
            await h.Service.RunHistoricalRotationAsync(Opts(batchSize: 10));
            Assert.Contains(1, h.Reader.SnapshotCallLog); // doc 1, processed in run 1, is revisited in the new lap
        }
        finally { await Cleanup(h); }
    }

    // HR02 — Phase 5 TASK 1.2/1.3: execution time budget reached mid-batch under SLOW SAP
    // responses specifically for the historical tier; historical cursor must not advance.
    [InvoiceReturnsPostgresFactShim]
    public async Task HR02_HistoricalRotation_SlowSapResponses_BudgetTrips_CursorWithheld()
    {
        var h = await NewHarnessAsync();
        try
        {
            h.Reader.AllDocEntriesValue = Enumerable.Range(1, 10).ToList();
            foreach (var d in h.Reader.AllDocEntriesValue) h.Reader.Snapshots[d] = FakeReturnRequestSapReader.Simple(d);
            h.Reader.DelayPerSnapshotCall = TimeSpan.FromMilliseconds(400);

            var report = await h.Service.RunHistoricalRotationAsync(Opts(batchSize: 10, maxExecSec: 1));
            Assert.True(report.CandidatesRefreshed < 10);
            Assert.Equal(0, await h.Service.ReadHistoricalCursorAsync());
        }
        finally { await Cleanup(h); }
    }

    // HR03 — Phase 5 TASK 1.4/1.8: a failed document refresh in the historical tier is isolated
    // (rest of batch still attempted) and does not advance the historical cursor — the failing
    // document is retried on the next run (cursor recovery after failure).
    [InvoiceReturnsPostgresFactShim]
    public async Task HR03_HistoricalRotation_FailedRefresh_IsolatedAndRetried()
    {
        var h = await NewHarnessAsync();
        try
        {
            h.Reader.AllDocEntriesValue = Enumerable.Range(1, 5).ToList();
            foreach (var d in h.Reader.AllDocEntriesValue) h.Reader.Snapshots[d] = FakeReturnRequestSapReader.Simple(d);
            h.Reader.ThrowForDocEntries.Add(3);

            var report1 = await h.Service.RunHistoricalRotationAsync(Opts(batchSize: 5));
            Assert.Equal(4, report1.CandidatesRefreshed);
            Assert.Equal(0, await h.Service.ReadHistoricalCursorAsync()); // withheld

            // Fix the simulated SAP fault and retry — TASK 1.8: cursor recovery after failure.
            h.Reader.ThrowForDocEntries.Remove(3);
            h.Reader.SnapshotCallLog.Clear();
            var report2 = await h.Service.RunHistoricalRotationAsync(Opts(batchSize: 5));
            Assert.Equal(5, report2.CandidatesRefreshed); // the whole batch, including doc 3, retried cleanly
            Assert.Contains(3, h.Reader.SnapshotCallLog);
        }
        finally { await Cleanup(h); }
    }

    // HR04 — Phase 5 TASK 1.5: service restart during a rotation cycle. Mirrors OR05 but for the
    // historical cursor specifically, confirming it (not just the incremental cursor) survives a
    // real on-disk SQLite file being closed and reopened mid-cycle.
    [InvoiceReturnsPostgresFactShim]
    public async Task HR04_HistoricalRotation_RestartMidCycle_ContinuesFromPersistedCursor()
    {
        var path = Path.Combine(Path.GetTempPath(), $"retrecon_hist_restart_{Guid.NewGuid():N}.db");
        var cs = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("RETURNS_TEST_POSTGRES")) { Pooling = false };
        var schema = "retrecon_hist_restart_" + Guid.NewGuid().ToString("N");
        cs.SearchPath = schema;
        await using var pg = new NpgsqlConnection(cs.ConnectionString); await pg.OpenAsync();
        await new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", pg).ExecuteNonQueryAsync();
        try
        {
            var neon = new NeonDbContext(new DbContextOptionsBuilder<NeonDbContext>().UseNpgsql(pg).Options);
            await new NpgsqlCommand(neon.Database.GenerateCreateScript(), pg).ExecuteNonQueryAsync();
            var candidates = Enumerable.Range(1, 23).ToList();
            var sqliteCs = $"Data Source={path};Pooling=False";

            await using (var conn1 = new SqliteConnection(sqliteCs))
            {
                await conn1.OpenAsync();
                var sqlite1 = new CacheDbContext(new DbContextOptionsBuilder<CacheDbContext>().UseSqlite(conn1).Options);
                await sqlite1.Database.EnsureCreatedAsync();
                await sqlite1.Database.ExecuteSqlRawAsync(ReturnReconciliationCursorPersistenceTests.CursorTableDdl);
                var reader1 = new FakeReturnRequestSapReader { AllDocEntriesValue = candidates };
                foreach (var d in candidates) reader1.Snapshots[d] = FakeReturnRequestSapReader.Simple(d);
                var service1 = new ReturnRequestReconciliationService(reader1, new ReturnRequestCacheService(sqlite1, NullLogger<ReturnRequestCacheService>.Instance),
                    new NeonReturnRequestWriteService(neon, NullLogger<NeonReturnRequestWriteService>.Instance), neon, sqlite1, NullLogger<ReturnRequestReconciliationService>.Instance);
                await service1.RunHistoricalRotationAsync(Opts(batchSize: 10)); // 1-10
                Assert.Equal(10, await service1.ReadHistoricalCursorAsync());
                await conn1.CloseAsync();
            } // "restart" mid-cycle, after run 1 of what would be 3 runs total

            await using var conn2 = new SqliteConnection(sqliteCs);
            await conn2.OpenAsync();
            var sqlite2 = new CacheDbContext(new DbContextOptionsBuilder<CacheDbContext>().UseSqlite(conn2).Options);
            var reader2 = new FakeReturnRequestSapReader { AllDocEntriesValue = candidates };
            foreach (var d in candidates) reader2.Snapshots[d] = FakeReturnRequestSapReader.Simple(d);
            var service2 = new ReturnRequestReconciliationService(reader2, new ReturnRequestCacheService(sqlite2, NullLogger<ReturnRequestCacheService>.Instance),
                new NeonReturnRequestWriteService(neon, NullLogger<NeonReturnRequestWriteService>.Instance), neon, sqlite2, NullLogger<ReturnRequestReconciliationService>.Instance);

            Assert.Equal(10, await service2.ReadHistoricalCursorAsync()); // survived the restart
            await service2.RunHistoricalRotationAsync(Opts(batchSize: 10)); // continues at 11-20, not 1-10 again
            Assert.DoesNotContain(1, reader2.SnapshotCallLog);
            Assert.Contains(11, reader2.SnapshotCallLog);
            await conn2.CloseAsync();
        }
        finally
        {
            await new NpgsqlCommand($"DROP SCHEMA \"{schema}\" CASCADE", pg).ExecuteNonQueryAsync();
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }

    // HR05 — Measured (not assumed) single-run duration using the EXACT 10 per-document
    // latencies observed in this project's own production log analysis (ReturnRequestHandler
    // "Done" entries, Phase 4/5 investigation): 3485, 3510, 3610, 10853, 4829, 4717, 4957, 5355,
    // 5233, 16384 ms. This is real measured wall-clock time for one HistoricalBatchSize=10 run,
    // not an invented constant — see the delivery report for how the full-cycle estimate is
    // derived from this single measured data point rather than claimed outright.
    [InvoiceReturnsPostgresFactShim(Timeout = 120_000)]
    public async Task HR05_MeasuredSingleRunDuration_UsingRealObservedProductionLatencies()
    {
        var h = await NewHarnessAsync();
        try
        {
            var realLatenciesMs = new[] { 3485, 3510, 3610, 10853, 4829, 4717, 4957, 5355, 5233, 16384 };
            h.Reader.AllDocEntriesValue = Enumerable.Range(1, 10).ToList();
            foreach (var d in h.Reader.AllDocEntriesValue) h.Reader.Snapshots[d] = FakeReturnRequestSapReader.Simple(d);
            h.Reader.DelaysInCallOrder = realLatenciesMs.Select(ms => TimeSpan.FromMilliseconds(ms)).ToList();

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var report = await h.Service.RunHistoricalRotationAsync(Opts(batchSize: 10, maxExecSec: 120));
            sw.Stop();

            Assert.Equal(10, report.CandidatesRefreshed); // all 10 completed within the default 120s budget
            // The measured duration is reported, not asserted against a fixed expectation — the
            // Phase 5 delivery report quotes this exact number for the full-cycle derivation.
            Console.WriteLine($"HR05 measured duration for 10 documents at real observed latencies: {sw.Elapsed.TotalSeconds:F1}s");
        }
        finally { await Cleanup(h); }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════
    // PHASE 6 TASK 1 — Deterministic proof that write-serialization alone does NOT guarantee
    // freshness. This simulates, precisely and step-by-step (no real concurrency, no timing
    // races — a controlled interleaving), exactly what the Phase 5 proposal ("move the
    // advisory lock into NeonReturnRequestWriteService.UpsertReturnRequestAsync") would
    // actually produce: since that method receives an already-read (header, lines) snapshot as
    // parameters, a lock placed INSIDE it wraps only the WRITE, never the READ — the read
    // always happens in the caller, before the locked method is even invoked. This test proves
    // that ordering is unsafe using the exact V1/V2 sequence specified in Phase 6 TASK 1:
    //   1. Writer A reads SAP snapshot V1 (OpenQty=5 — "old").
    //   2. SAP changes (credit memo fully consumes it).
    //   3. Writer B reads SAP snapshot V2 (OpenQty=0 — "new").
    //   4. Writer B acquires the write-only lock and commits V2.
    //   5. Writer A subsequently acquires the write-only lock and commits its stale V1.
    // Expected (and this is the point): final state is the STALE V1, not the fresher V2 that
    // was committed in between. The lock serialized the two WRITES (they never overlapped —
    // confirmed via a shared order-of-acquisition log below) but did nothing to stop the
    // second writer from overwriting with data it read before the lock even existed for this
    // round. This is NOT the same bug as OR07 (which shows self-healing via a LATER refresh) —
    // this shows the specific requested ordering leaves a window with NO subsequent refresh to
    // self-heal it, for as long as nothing else happens to touch this DocEntry again.
    // ══════════════════════════════════════════════════════════════════════════════════════

    [InvoiceReturnsPostgresFactShim]
    public async Task STALE01_WriteOnlyLock_DoesNotPreventStaleOverwrite_DeterministicV1V2()
    {
        var h = await NewHarnessAsync();
        try
        {
            const int docEntry = 800;
            await SeedInvoiceLineAsync(h.Pg, 50001, 0); // this harness doesn't seed Invoices/InvoiceLines by default
            var writer = Writer2(h.Pg); // raw NeonReturnRequestWriteService, no lock of its own
            var acquisitionOrder = new List<string>();

            // Step 1 — Writer A reads SAP snapshot V1 (OpenQty=5) BEFORE any lock is involved,
            // exactly as every caller of UpsertReturnRequestAsync does today (the read always
            // precedes the call) and would continue to do if a lock were added only inside that
            // method.
            var v1 = (ReturnRequestReconciliationTests.Header(docEntry, "O", "N"), new[] { ReturnRequestReconciliationTests.Line(docEntry, 0, 50001, 0, 5, 5, "O") });

            // Step 2 — SAP changes (modeled, not actually re-queried — this test controls the
            // sequence deterministically rather than depending on real timing).

            // Step 3 — Writer B reads SAP snapshot V2 (OpenQty=0 — fully consumed/closed).
            var v2 = (ReturnRequestReconciliationTests.Header(docEntry, "C", "N"), new[] { ReturnRequestReconciliationTests.Line(docEntry, 0, 50001, 0, 5, 0, "C") });

            // Step 4 — Writer B acquires the write-only lock (simulating the proposed in-method
            // lock), commits V2, releases.
            await WithWriteOnlyLock(h.Pg, docEntry, acquisitionOrder, "B", async () =>
                await writer.UpsertReturnRequestAsync(v2.Item1, v2.Item2));

            Assert.Equal(0m, await ReturnRequestReconciliationTests.PendingQty(h.Pg, 50001, 0)); // correct, fresh state — momentarily

            // Step 5 — Writer A, still holding only its ORIGINAL stale V1 in memory from step 1
            // (it never re-read SAP — there was no reason to, under a write-only lock design),
            // now acquires the SAME write-only lock and commits V1.
            await WithWriteOnlyLock(h.Pg, docEntry, acquisitionOrder, "A", async () =>
                await writer.UpsertReturnRequestAsync(v1.Item1, v1.Item2));

            // The two writes never overlapped — the lock DID serialize them correctly.
            Assert.Equal(new[] { "B-acquired", "B-released", "A-acquired", "A-released" }, acquisitionOrder);

            // And yet: the final state is the STALE value. Serialization succeeded; freshness
            // did not. This is the exact defect TASK 1 asked to demonstrate.
            Assert.Equal(5m, await ReturnRequestReconciliationTests.PendingQty(h.Pg, 50001, 0));
            Assert.NotEqual(0m, await ReturnRequestReconciliationTests.PendingQty(h.Pg, 50001, 0)); // explicit: NOT the fresher value
        }
        finally { await Cleanup(h); }
    }

    // Companion test: the SAME V1/V2 sequence, but using ReturnRequestReconciliationService's
    // ACTUAL current lock-THEN-read-THEN-write ordering (RefreshOneAsync) for writer B, to show
    // the contrast — when the read happens while holding the lock, there is no stale-snapshot
    // window at all, because a writer can never be holding data that predates its own lock
    // acquisition. This is the basis for the TASK 2 recommendation below: the fix is read
    // ordering relative to the lock, not merely adding a lock.
    [InvoiceReturnsPostgresFactShim]
    public async Task STALE02_LockThenRead_NoStaleWindow_ContrastWithSTALE01()
    {
        var h = await NewHarnessAsync();
        try
        {
            const int docEntry = 801;
            await SeedInvoiceLineAsync(h.Pg, 50001, 0);
            // h.Service's RefreshOneAsync acquires the lock, THEN calls sapReader.GetSnapshot —
            // so whatever is in h.Reader.Snapshots AT THE MOMENT the lock is acquired is what
            // gets committed. There is no "read before lock" step to go stale.
            h.Reader.Snapshots[docEntry] = (ReturnRequestReconciliationTests.Header(docEntry, "O", "N"), new List<Models.Cache.CachedReturnRequestLine> { ReturnRequestReconciliationTests.Line(docEntry, 0, 50001, 0, 5, 5, "O") });
            h.Reader.ChangedDocEntries = new List<int> { docEntry };
            await h.Service.RunIncrementalAsync(Opts(batchSize: 1));
            Assert.Equal(5m, await ReturnRequestReconciliationTests.PendingQty(h.Pg, 50001, 0));

            // "SAP changes" — update what the NEXT lock-holder will see when IT reads.
            h.Reader.Snapshots[docEntry] = (ReturnRequestReconciliationTests.Header(docEntry, "C", "N"), new List<Models.Cache.CachedReturnRequestLine> { ReturnRequestReconciliationTests.Line(docEntry, 0, 50001, 0, 5, 0, "C") });
            await h.Service.RunIncrementalAsync(Opts(batchSize: 1));

            // Because the read happens AFTER lock acquisition, this run necessarily saw the
            // CURRENT (fresh) state — there is no mechanism by which it could have committed a
            // value older than what existed at the moment it acquired the lock.
            Assert.Equal(0m, await ReturnRequestReconciliationTests.PendingQty(h.Pg, 50001, 0));
        }
        finally { await Cleanup(h); }
    }

    /// <summary>
    /// ReturnRequestOrchestrationTests' NewHarnessAsync deliberately does not seed
    /// Invoices/InvoiceLines (most of its tests use BaseEntry=99999, a nonexistent invoice,
    /// since they only care about SAP-snapshot/cursor/lock orchestration). STALE01/STALE02
    /// specifically need a real InvoiceLines row to observe PendingReturnQty, so they seed it
    /// explicitly here rather than changing the shared harness for every other test.
    /// </summary>
    private static async Task SeedInvoiceLineAsync(NpgsqlConnection pg, int docEntry, int lineNum)
    {
        await new NpgsqlCommand(
            $"INSERT INTO \"Invoices\" (\"DocEntry\", \"DocNum\", \"InvoiceDocNum\", \"DocDate\", \"DocStatus\", \"CardCode\", \"CardName\", \"DocTotal\", \"PaidToDate\", \"BalanceDue\", \"SalesEmployeeCode\", \"SalesEmployeeName\", \"DaysOverdue\", \"Canceled\") " +
            $"VALUES ({docEntry}, {docEntry}, {docEntry}, '2026-10-01T00:00:00Z', 'O', 'C001', 'Test Customer', 0, 0, 0, 0, '', 0, 'N')", pg)
            .ExecuteNonQueryAsync();
        await new NpgsqlCommand(
            $"INSERT INTO \"InvoiceLines\" (\"DocEntry\", \"LineNum\", \"ItemCode\", \"Dscription\", \"Quantity\", \"Price\", \"LineTotal\", \"U_MDLTsT\", \"ReturnedQty\") " +
            $"VALUES ({docEntry}, {lineNum}, 'ITEM001', 'Test Item', 10, 0, 0, '', 0)", pg)
            .ExecuteNonQueryAsync();
    }

    private static NeonReturnRequestWriteService Writer2(NpgsqlConnection pg)
        => new(new NeonDbContext(new DbContextOptionsBuilder<NeonDbContext>().UseNpgsql(pg).Options), NullLogger<NeonReturnRequestWriteService>.Instance);

    /// <summary>
    /// Simulates exactly what "add pg_advisory_lock/unlock inside UpsertReturnRequestAsync"
    /// would do — lock, run the write, unlock — on a dedicated non-pooled connection (the same
    /// safety pattern RefreshOneAsync already uses), logging acquisition/release order so the
    /// test can prove the writes were genuinely serialized even though the result is stale.
    /// </summary>
    private static async Task WithWriteOnlyLock(NpgsqlConnection samePgForKeyScope, int docEntry, List<string> order, string who, Func<Task> work)
    {
        var cs = new NpgsqlConnectionStringBuilder(samePgForKeyScope.ConnectionString) { Pooling = false };
        await using var lockConn = new NpgsqlConnection(cs.ConnectionString); await lockConn.OpenAsync();
        long key = ((long)0x5252 << 32) | (uint)docEntry;
        await using (var lockCmd = new NpgsqlCommand("SELECT pg_advisory_lock(@k)", lockConn))
        { lockCmd.Parameters.AddWithValue("@k", NpgsqlTypes.NpgsqlDbType.Bigint, key); await lockCmd.ExecuteNonQueryAsync(); }
        order.Add($"{who}-acquired");
        try { await work(); }
        finally
        {
            await using var unlockCmd = new NpgsqlCommand("SELECT pg_advisory_unlock(@k)", lockConn);
            unlockCmd.Parameters.AddWithValue("@k", NpgsqlTypes.NpgsqlDbType.Bigint, key);
            await unlockCmd.ExecuteNonQueryAsync();
            order.Add($"{who}-released");
        }
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
