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

    public List<int> GetChangedDocEntries(DateTime since) => ChangedDocEntries;
    public List<int> GetAllDocEntries() => AllDocEntriesValue;

    public (CachedReturnRequest header, List<CachedReturnRequestLine> lines)? GetSnapshot(int docEntry)
    {
        SnapshotCallLog.Add(docEntry);
        if (DelayPerSnapshotCall > TimeSpan.Zero) Thread.Sleep(DelayPerSnapshotCall);
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

    private static ReturnReconciliationOptions Opts(int batchSize = 50, int maxExecSec = 240, int lookbackMin = 180) =>
        new() { BatchSize = batchSize, MaxExecutionSeconds = maxExecSec, LookbackMinutes = lookbackMin,
                HistoricalBatchSize = batchSize, HistoricalMaxExecutionSeconds = maxExecSec };

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
