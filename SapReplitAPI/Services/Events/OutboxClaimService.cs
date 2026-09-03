using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

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

    // ── Batch claim ─────────────────────────────────────────────────────────
    // Atomically marks up to batchSize Pending rows as Processing.
    // Uses UPDLOCK/READPAST/ROWLOCK so concurrent pollers skip locked rows.
    // Returns the claimed rows via OUTPUT clause — no second SELECT needed.

    public async Task<List<SapOutboxEvent>> ClaimBatchAsync(int batchSize = 50, CancellationToken ct = default)
    {
        const string sql = """
            WITH cte AS (
                SELECT TOP (@batchSize) *
                FROM  dbo.SapEventOutbox WITH (UPDLOCK, READPAST, ROWLOCK)
                WHERE Status = N'Pending'
                  AND (NextAttemptAtUtc IS NULL OR NextAttemptAtUtc <= SYSUTCDATETIME())
                ORDER BY Id
            )
            UPDATE cte
            SET
                Status       = N'Processing',
                ClaimedAtUtc = SYSUTCDATETIME(),
                AttemptCount = AttemptCount + 1,
                ClaimedBy    = @workerIdentity
            OUTPUT
                INSERTED.Id, INSERTED.EventId, INSERTED.ObjectType, INSERTED.TransactionType,
                INSERTED.DocEntry, INSERTED.KeyValues, INSERTED.CreatedAtUtc, INSERTED.AttemptCount;
            """;

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd  = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@batchSize",      batchSize);
        cmd.Parameters.AddWithValue("@workerIdentity", _workerIdentity);

        var events = new List<SapOutboxEvent>();
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
                AttemptCount:    reader.GetInt32(7)
            ));
        }

        return events;
    }

    // ── Mark Done ───────────────────────────────────────────────────────────

    public async Task MarkDoneAsync(long id, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE dbo.SapEventOutbox
            SET Status         = N'Done',
                ProcessedAtUtc = SYSUTCDATETIME(),
                ClaimedAtUtc   = NULL,
                ClaimedBy      = NULL
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

        const string sql = """
            UPDATE dbo.SapEventOutbox
            SET
                Status           = CASE WHEN AttemptCount >= 3 THEN N'Failed' ELSE N'Pending' END,
                NextAttemptAtUtc = CASE
                                       WHEN AttemptCount = 1 THEN DATEADD(SECOND,  10, SYSUTCDATETIME())
                                       WHEN AttemptCount = 2 THEN DATEADD(SECOND,  60, SYSUTCDATETIME())
                                       ELSE NULL
                                   END,
                ClaimedAtUtc     = NULL,
                ClaimedBy        = NULL,
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
}

public sealed record OutboxHealthSnapshot(
    int       Pending,
    int       Processing,
    int       Done,
    int       Failed,
    DateTime? LastProcessedUtc,
    long?     OldestPendingAgeSec
);
