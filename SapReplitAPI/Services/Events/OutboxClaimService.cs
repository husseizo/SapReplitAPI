using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using System.Linq;

namespace SapReplitAPI.Services.Events;

/// <summary>
/// All SQL operations against MolasIntegration.dbo.SapEventOutbox.
/// Registered as Singleton. Creates a new SqlConnection per method call
/// and disposes it immediately — never holds an open shared connection.
/// Permissions required: SELECT + UPDATE on dbo.SapEventOutbox (login: SapReplitOutboxApp).
/// </summary>
public sealed class OutboxClaimService
{
    private readonly string _connectionString;
    private readonly string _workerIdentity;
    private readonly ILogger<OutboxClaimService> _logger;

    public OutboxClaimService(IConfiguration config, ILogger<OutboxClaimService> logger)
    {
        var cs = config.GetConnectionString("MolasIntegration")
                 ?? throw new InvalidOperationException(
                     "Connection string 'MolasIntegration' is missing. " +
                     "Set env var MOLASINTEGRATION_CONNSTR on the host machine.");
        _connectionString = cs;
        _workerIdentity   = $"{AppDomain.CurrentDomain.FriendlyName}@{Environment.MachineName}";
        _logger           = logger;
    }

    // ── Stuck-lease recovery ────────────────────────────────────────────────
    // Resets any Processing row older than 5 minutes back to Pending.
    // Called at the start of every poll cycle before claiming new events.

    public async Task ResetOrphanedAsync(CancellationToken ct = default)
    {
        const string sql = """
            UPDATE dbo.SapEventOutbox
            SET
                Status       = N'Pending',
                ClaimedAtUtc = NULL,
                ClaimedBy    = NULL,
                LastError    = ISNULL(LastError, N'') + N' | Orphaned at '
                               + CONVERT(NVARCHAR, SYSUTCDATETIME(), 126)
                               + N' by recovery sweep'
            WHERE Status      = N'Processing'
              AND ClaimedAtUtc < DATEADD(MINUTE, -5, SYSUTCDATETIME());
            """;

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd  = new SqlCommand(sql, conn);
        int n = await cmd.ExecuteNonQueryAsync(ct);
        if (n > 0)
            _logger.LogWarning("[OutboxClaim] Reset {Count} orphaned Processing row(s) to Pending.", n);
    }

    // ── Priority mapping ─────────────────────────────────────────────────────
    // DERIVED from ObjectType (not a stored column) — smallest safe change, stays
    // deterministic/indexable-by-filter/observable, and is forward-compatible: adding
    // OITM/ITM1/OPLN later only means adding CASE branches here, no schema migration.
    //
    // P0 — operational/latency-sensitive: Delivery(15), Return(16), Sales Order(17,
    //       all of A/U/C), GRPO(20), Goods Receipt(59), Goods Issue(60), Stock
    //       Transfer(67), A/R Invoice(13) and Credit Memo(14) — both carry real
    //       operational effects (OINM stock posting, ZF snapshot, inventory refresh).
    // P1 — business/financial: Incoming Payment(24), Return Request(234000031).
    // P2 — product/catalog/pricing: reserved for future OITM/ITM1/OPLN. No currently
    //       registered ObjectType maps here — this branch exists only so a future
    //       handler's events sort behind P0/P1 without any further schema change.
    private const string PriorityCaseSql = """
        CASE
            WHEN ObjectType IN ('13','14','15','16','17','20','59','60','67') THEN 0
            WHEN ObjectType IN ('24','234000031') THEN 1
            ELSE 2
        END
        """;

    // ── Batch claim (priority-aware, fairness-guaranteed) ───────────────────
    //
    // Fixes the starvation mechanism identified by the architecture review: under
    // strict FIFO, a large P1/P2 backlog (e.g. thousands of future OITM/ITM1 events)
    // could sit ahead of a newly-arrived P0 event (Delivery, Sales Order, ...).
    //
    // Fairness rule (deterministic, documented): each claim cycle reserves at least
    // minNonP0PerCycle slots for P1/P2 work, and P0 gets the REST of the batch — never
    // more than (batchSize - minNonP0PerCycle) P0 rows in one cycle. Two independent
    // claim queries run, each with its own WHERE clause:
    //   1. P0 query: WHERE priority=0 — completely unaware of how large the P1/P2
    //      backlog is, so a P0 row is claimed on its own merits, never stuck behind
    //      P1/P2 regardless of backlog size (this is what makes PRI_04 provably true,
    //      not just true by coincidence of ordering).
    //   2. Non-P0 query: WHERE priority>0 — claims up to (batchSize - P0 claimed),
    //      so if P0 backlog is small/empty, P1/P2 gets MORE than its reserved floor
    //      and drains faster; if P0 backlog is large, P1/P2 still always gets its
    //      floor every single cycle — it can never be starved to zero.
    // Concurrency stays at 1: both queries run sequentially on the same connection,
    // inside the same single-threaded poll cycle — no new workers, no parallel SAP
    // COM access, existing UPDLOCK/READPAST/ROWLOCK semantics unchanged.
    //
    // Returned list is ordered P0-first so the poller's sequential foreach (which
    // processes events in list order) also processes P0 first within one batch.
    public const int DefaultMinNonP0PerCycle = 5;

    public async Task<List<SapOutboxEvent>> ClaimBatchAsync(
        int batchSize = 50, CancellationToken ct = default)
        => await ClaimBatchAsync(batchSize, DefaultMinNonP0PerCycle, ct);

    public async Task<List<SapOutboxEvent>> ClaimBatchAsync(
        int batchSize, int minNonP0PerCycle, CancellationToken ct = default)
    {
        if (batchSize <= 0) return new List<SapOutboxEvent>();
        int p0Share = Math.Max(0, batchSize - Math.Max(0, minNonP0PerCycle));

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        var p0Events = await ClaimByPriorityAsync(conn, p0Share, p0Only: true, ct);

        int remaining = batchSize - p0Events.Count;
        var nonP0Events = remaining > 0
            ? await ClaimByPriorityAsync(conn, remaining, p0Only: false, ct)
            : new List<SapOutboxEvent>();

        var combined = new List<SapOutboxEvent>(p0Events.Count + nonP0Events.Count);
        combined.AddRange(p0Events);
        combined.AddRange(nonP0Events);
        return combined;
    }

    private async Task<List<SapOutboxEvent>> ClaimByPriorityAsync(
        SqlConnection conn, int take, bool p0Only, CancellationToken ct)
    {
        var events = new List<SapOutboxEvent>();
        if (take <= 0) return events;

        string priorityFilter = p0Only
            ? $"({PriorityCaseSql}) = 0"
            : $"({PriorityCaseSql}) > 0";

        string sql = $"""
            WITH ranked AS (
                SELECT *, ({PriorityCaseSql}) AS Priority
                FROM  dbo.SapEventOutbox WITH (UPDLOCK, READPAST, ROWLOCK)
                WHERE Status = N'Pending'
                  AND (NextAttemptAtUtc IS NULL OR NextAttemptAtUtc <= SYSUTCDATETIME())
                  AND {priorityFilter}
            ),
            cte AS (
                SELECT TOP (@take) * FROM ranked ORDER BY Priority, Id
            )
            UPDATE cte
            SET
                Status       = N'Processing',
                ClaimedAtUtc = SYSUTCDATETIME(),
                AttemptCount = AttemptCount + 1,
                ClaimedBy    = @workerIdentity
            OUTPUT
                INSERTED.Id, INSERTED.EventId, INSERTED.ObjectType, INSERTED.TransactionType,
                INSERTED.DocEntry, INSERTED.KeyValues, INSERTED.CreatedAtUtc, INSERTED.AttemptCount,
                INSERTED.Priority;
            """;

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@take",           take);
        cmd.Parameters.AddWithValue("@workerIdentity", _workerIdentity);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            events.Add(new SapOutboxEvent(
                Id:              reader.GetInt64(0),
                EventId:         reader.GetGuid(1),
                ObjectType:      reader.GetString(2),
                TransactionType: reader.GetString(3).Trim(),
                DocEntry:        reader.IsDBNull(4) ? null : reader.GetInt32(4),
                KeyValues:       reader.IsDBNull(5) ? null : reader.GetString(5),
                CreatedAtUtc:    reader.GetDateTime(6),
                AttemptCount:    reader.GetInt32(7),
                Priority:        reader.GetInt32(8)
            ));
        }

        return events;
    }

    /// <summary>Exposed for tests/diagnostics — same mapping the claim query uses.</summary>
    public static int ResolvePriority(string objectType) => objectType switch
    {
        "13" or "14" or "15" or "16" or "17" or "20" or "59" or "60" or "67" => 0,
        "24" or "234000031" => 1,
        _ => 2
    };

    /// <summary>
    /// Pure, DB-free extraction of the exact selection algorithm ClaimByPriorityAsync's
    /// two SQL queries implement (P0 query: WHERE Priority=0; non-P0 query: WHERE
    /// Priority&gt;0, claiming batchSize-P0Claimed). Kept in lock-step with the SQL by
    /// design — used directly by PRI_* tests to prove the fairness/priority contract
    /// deterministically and fast, without a live SQL Server.
    /// </summary>
    public static List<SapOutboxEvent> SelectClaimOrder(
        IEnumerable<SapOutboxEvent> pending, int batchSize, int minNonP0PerCycle)
    {
        int p0Share = Math.Max(0, batchSize - Math.Max(0, minNonP0PerCycle));

        var p0 = pending
            .Where(e => ResolvePriority(e.ObjectType) == 0)
            .OrderBy(e => e.Id)
            .Take(p0Share)
            .ToList();

        int remaining = batchSize - p0.Count;
        var nonP0 = remaining > 0
            ? pending
                .Where(e => ResolvePriority(e.ObjectType) > 0)
                .OrderBy(e => ResolvePriority(e.ObjectType))
                .ThenBy(e => e.Id)
                .Take(remaining)
                .ToList()
            : new List<SapOutboxEvent>();

        var result = new List<SapOutboxEvent>(p0.Count + nonP0.Count);
        result.AddRange(p0);
        result.AddRange(nonP0);
        return result;
    }

    // ── Mark Done ───────────────────────────────────────────────────────────

    public async Task MarkDoneAsync(long id, CancellationToken ct = default)
    {
        // ClaimedAtUtc/ClaimedBy are intentionally PRESERVED here (not nulled) — the row
        // is terminal, so the last successful claim timestamp is pure historical value:
        // it's what makes ClaimLag (ClaimedAtUtc-CreatedAtUtc) and ProcessingDuration
        // (ProcessedAtUtc-ClaimedAtUtc) measurable after the fact. Previously this cleared
        // both columns, which is why no historical Done row ever retained ClaimedAtUtc.
        const string sql = """
            UPDATE dbo.SapEventOutbox
            SET Status         = N'Done',
                ProcessedAtUtc = SYSUTCDATETIME()
            WHERE Id = @id;
            """;

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd  = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── Mark Failed / Schedule Retry ────────────────────────────────────────
    // AttemptCount was already incremented at claim time.
    // Attempt 1 failure → Pending, retry +10s
    // Attempt 2 failure → Pending, retry +60s
    // Attempt 3+ failure → Failed (permanent)

    public async Task MarkFailedAsync(long id, int attemptCount, string error, CancellationToken ct = default)
    {
        string truncatedError = error.Length > 4000 ? error[..4000] : error;

        // ClaimedAtUtc/ClaimedBy are cleared ONLY on the retry path (row returns to
        // Pending — it is genuinely no longer claimed). On the terminal Failed path
        // (AttemptCount>=3) they are PRESERVED, same rationale as MarkDoneAsync: pure
        // historical value for ClaimLag/ProcessingDuration measurement, no operational
        // downside since the row will never be claimed again. ProcessedAtUtc is now
        // also set on terminal Failed, matching Done's "when this row reached a
        // terminal state" semantic (previously never set for Failed rows at all).
        const string sql = """
            UPDATE dbo.SapEventOutbox
            SET
                Status           = CASE WHEN AttemptCount >= 3 THEN N'Failed' ELSE N'Pending' END,
                NextAttemptAtUtc = CASE
                                       WHEN AttemptCount = 1 THEN DATEADD(SECOND,  10, SYSUTCDATETIME())
                                       WHEN AttemptCount = 2 THEN DATEADD(SECOND,  60, SYSUTCDATETIME())
                                       ELSE NULL
                                   END,
                ProcessedAtUtc   = CASE WHEN AttemptCount >= 3 THEN SYSUTCDATETIME() ELSE ProcessedAtUtc END,
                ClaimedAtUtc     = CASE WHEN AttemptCount >= 3 THEN ClaimedAtUtc ELSE NULL END,
                ClaimedBy        = CASE WHEN AttemptCount >= 3 THEN ClaimedBy ELSE NULL END,
                LastError        = @error
            WHERE Id = @id;
            """;

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd  = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@id",    id);
        cmd.Parameters.AddWithValue("@error", truncatedError);
        await cmd.ExecuteNonQueryAsync(ct);

        if (attemptCount >= 3)
            _logger.LogError("[OutboxClaim] Event Id={Id} permanently Failed after {Attempts} attempts. Error: {Error}",
                id, attemptCount, truncatedError);
        else
            _logger.LogWarning("[OutboxClaim] Event Id={Id} attempt {Attempt} failed; retry scheduled. Error: {Error}",
                id, attemptCount, truncatedError);
    }

    // ── Health query ────────────────────────────────────────────────────────
    // Returns a lightweight snapshot used by startup diagnostics and the health endpoint.
    // Never exposes the connection string or credentials in its output.

    public async Task<OutboxHealthSnapshot> QueryHealthAsync(CancellationToken ct = default)
    {
        const string sql = """
            SELECT
                SUM(CASE WHEN Status = N'Pending'    THEN 1 ELSE 0 END) AS Pending,
                SUM(CASE WHEN Status = N'Processing' THEN 1 ELSE 0 END) AS Processing,
                SUM(CASE WHEN Status = N'Done'       THEN 1 ELSE 0 END) AS Done,
                SUM(CASE WHEN Status = N'Failed'     THEN 1 ELSE 0 END) AS Failed,
                MAX(CASE WHEN Status = N'Done' THEN ProcessedAtUtc END)  AS LastProcessedUtc,
                MIN(CASE WHEN Status = N'Pending'
                    THEN DATEDIFF(SECOND, CreatedAtUtc, SYSUTCDATETIME())
                    END)                                                 AS OldestPendingAgeSec
            FROM dbo.SapEventOutbox;
            """;

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd  = new SqlCommand(sql, conn);
        await using var rdr  = await cmd.ExecuteReaderAsync(ct);

        if (!await rdr.ReadAsync(ct))
            return new OutboxHealthSnapshot(0, 0, 0, 0, null, null);

        return new OutboxHealthSnapshot(
            Pending:           rdr.IsDBNull(0) ? 0 : rdr.GetInt32(0),
            Processing:        rdr.IsDBNull(1) ? 0 : rdr.GetInt32(1),
            Done:              rdr.IsDBNull(2) ? 0 : rdr.GetInt32(2),
            Failed:            rdr.IsDBNull(3) ? 0 : rdr.GetInt32(3),
            LastProcessedUtc:  rdr.IsDBNull(4) ? null : rdr.GetDateTime(4),
            OldestPendingAgeSec: rdr.IsDBNull(5) ? null : (long?)rdr.GetInt32(5)
        );
    }
    // ── Detailed diagnostics (percentiles + breakdowns) ─────────────────────
    // Manual ROW_NUMBER-based percentiles — PERCENTILE_CONT/STRING_AGG are not
    // reliably available on this SQL Server's compatibility level (confirmed during
    // the architecture review), so this uses the same portable pattern already
    // validated against this instance. Windowed to the last @hours hours by
    // CreatedAtUtc so the query stays cheap regardless of total retained history.
    public async Task<OutboxDetailedHealth> QueryDetailedHealthAsync(int hours = 24, CancellationToken ct = default)
    {
        var counts = await QueryHealthAsync(ct);

        const string latencySql = """
            WITH x AS (
                SELECT
                    DATEDIFF(MILLISECOND, CreatedAtUtc, ClaimedAtUtc)   AS ClaimLagMs,
                    DATEDIFF(MILLISECOND, ClaimedAtUtc, ProcessedAtUtc) AS ProcessingMs,
                    ROW_NUMBER() OVER (ORDER BY DATEDIFF(MILLISECOND, CreatedAtUtc, ClaimedAtUtc))   AS rnClaim,
                    ROW_NUMBER() OVER (ORDER BY DATEDIFF(MILLISECOND, ClaimedAtUtc, ProcessedAtUtc)) AS rnProc,
                    COUNT(*) OVER() AS n
                FROM dbo.SapEventOutbox
                WHERE ProcessedAtUtc IS NOT NULL AND ClaimedAtUtc IS NOT NULL
                  AND CreatedAtUtc >= DATEADD(HOUR, -@hours, SYSUTCDATETIME())
            )
            SELECT
                MAX(n) AS N,
                MAX(CASE WHEN rnClaim = CEILING(0.50*n) THEN ClaimLagMs END) AS ClaimP50,
                MAX(CASE WHEN rnClaim = CEILING(0.95*n) THEN ClaimLagMs END) AS ClaimP95,
                MAX(CASE WHEN rnClaim = CEILING(0.99*n) THEN ClaimLagMs END) AS ClaimP99,
                MAX(CASE WHEN rnProc  = CEILING(0.50*n) THEN ProcessingMs END) AS ProcP50,
                MAX(CASE WHEN rnProc  = CEILING(0.95*n) THEN ProcessingMs END) AS ProcP95,
                MAX(CASE WHEN rnProc  = CEILING(0.99*n) THEN ProcessingMs END) AS ProcP99
            FROM x;
            """;

        // Priority CASE duplicated inline here (matches PriorityCaseSql exactly) —
        // raw string literals can't be concatenated mid-literal, so this stays a
        // plain verbatim string instead.
        const string breakdownSql = @"
            SELECT ObjectType, TransactionType,
                   CASE
                       WHEN ObjectType IN ('13','14','15','16','17','20','59','60','67') THEN 0
                       WHEN ObjectType IN ('24','234000031') THEN 1
                       ELSE 2
                   END AS Priority,
                   COUNT(*) AS Cnt
            FROM dbo.SapEventOutbox
            WHERE CreatedAtUtc >= DATEADD(HOUR, -@hours, SYSUTCDATETIME())
            GROUP BY ObjectType, TransactionType
            ORDER BY Cnt DESC;";

        const string throughputSql = """
            SELECT COUNT(*) FROM dbo.SapEventOutbox
            WHERE Status = N'Done' AND ProcessedAtUtc >= DATEADD(MINUTE, -1, SYSUTCDATETIME());
            """;

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        long? claimP50 = null, claimP95 = null, claimP99 = null, procP50 = null, procP95 = null, procP99 = null;
        int sampleN = 0;
        await using (var cmd = new SqlCommand(latencySql, conn))
        {
            cmd.Parameters.AddWithValue("@hours", hours);
            await using var rdr = await cmd.ExecuteReaderAsync(ct);
            if (await rdr.ReadAsync(ct) && !rdr.IsDBNull(0))
            {
                sampleN  = rdr.GetInt32(0);
                claimP50 = rdr.IsDBNull(1) ? null : rdr.GetInt32(1);
                claimP95 = rdr.IsDBNull(2) ? null : rdr.GetInt32(2);
                claimP99 = rdr.IsDBNull(3) ? null : rdr.GetInt32(3);
                procP50  = rdr.IsDBNull(4) ? null : rdr.GetInt32(4);
                procP95  = rdr.IsDBNull(5) ? null : rdr.GetInt32(5);
                procP99  = rdr.IsDBNull(6) ? null : rdr.GetInt32(6);
            }
        }

        var breakdown = new List<OutboxBreakdownRow>();
        await using (var cmd = new SqlCommand(breakdownSql, conn))
        {
            cmd.Parameters.AddWithValue("@hours", hours);
            await using var rdr = await cmd.ExecuteReaderAsync(ct);
            while (await rdr.ReadAsync(ct))
            {
                breakdown.Add(new OutboxBreakdownRow(
                    ObjectType:      rdr.GetString(0),
                    TransactionType: rdr.GetString(1).Trim(),
                    Priority:        rdr.GetInt32(2),
                    Count:           rdr.GetInt32(3)
                ));
            }
        }

        int perMinute;
        await using (var cmd = new SqlCommand(throughputSql, conn))
        {
            perMinute = (int)(await cmd.ExecuteScalarAsync(ct) ?? 0);
        }

        return new OutboxDetailedHealth(
            Counts:            counts,
            WindowHours:       hours,
            SampleSize:        sampleN,
            ClaimLagP50Ms:     claimP50,
            ClaimLagP95Ms:     claimP95,
            ClaimLagP99Ms:     claimP99,
            ProcessingP50Ms:   procP50,
            ProcessingP95Ms:   procP95,
            ProcessingP99Ms:   procP99,
            DoneLastMinute:    perMinute,
            Breakdown:         breakdown
        );
    }
}

public sealed record OutboxBreakdownRow(
    string ObjectType,
    string TransactionType,
    int    Priority,
    int    Count
);

public sealed record OutboxDetailedHealth(
    OutboxHealthSnapshot        Counts,
    int                         WindowHours,
    int                         SampleSize,
    long?                       ClaimLagP50Ms,
    long?                       ClaimLagP95Ms,
    long?                       ClaimLagP99Ms,
    long?                       ProcessingP50Ms,
    long?                       ProcessingP95Ms,
    long?                       ProcessingP99Ms,
    int                         DoneLastMinute,
    List<OutboxBreakdownRow>    Breakdown
);

public sealed record OutboxHealthSnapshot(
    int       Pending,
    int       Processing,
    int       Done,
    int       Failed,
    DateTime? LastProcessedUtc,
    long?     OldestPendingAgeSec
);
