using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using SapReplitAPI.Models.ZoneFulfillment;
using System.Text.Json;

namespace SapReplitAPI.Services.ZoneFulfillment;

/// <summary>
/// Durable write-through audit log for ZF admin actions.
/// Two-phase write: InsertAsync (Pending) before mutation; SetPostStateAsync after.
/// Append-only — never UPDATE Result to anything other than terminal values.
/// </summary>
public class ZfAdminAuditRepository
{
    private readonly string _cs;

    protected ZfAdminAuditRepository() => _cs = "";

    public ZfAdminAuditRepository(IConfiguration cfg)
        => _cs = cfg.GetConnectionString("MolasIntegration")
            ?? throw new InvalidOperationException("MolasIntegration connection string missing.");

    // ── Startup DDL probe ─────────────────────────────────────────────────────

    public async Task EnsureTableAsync(CancellationToken ct = default)
    {
        const string sql = """
            IF NOT EXISTS (
                SELECT 1 FROM sys.tables
                WHERE  name = 'ZfAdminAuditLog' AND schema_id = SCHEMA_ID('dbo'))
            BEGIN
                CREATE TABLE dbo.ZfAdminAuditLog (
                    Id               BIGINT           IDENTITY(1,1) PRIMARY KEY,
                    ActionId         UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
                    SoDocEntry       INT              NOT NULL,
                    RequestId        UNIQUEIDENTIFIER NULL,
                    ActionType       NVARCHAR(80)     NOT NULL,
                    ReasonCode       NVARCHAR(120)    NULL,
                    RequestedBy      NVARCHAR(200)    NOT NULL,
                    RequestedAtUtc   DATETIME2        NOT NULL,
                    ExecutedAtUtc    DATETIME2        NULL,
                    Result           NVARCHAR(20)     NOT NULL,
                    BeforeStateJson  NVARCHAR(MAX)    NULL,
                    AfterStateJson   NVARCHAR(MAX)    NULL,
                    EvidenceJson     NVARCHAR(MAX)    NULL,
                    ErrorCode        NVARCHAR(80)     NULL,
                    ErrorMessage     NVARCHAR(1000)   NULL
                );
                CREATE INDEX IX_ZfAdminAuditLog_SoDocEntry
                    ON dbo.ZfAdminAuditLog (SoDocEntry, RequestedAtUtc DESC);
                CREATE INDEX IX_ZfAdminAuditLog_ActionId
                    ON dbo.ZfAdminAuditLog (ActionId);
            END;
            """;
        await using var conn = new SqlConnection(_cs);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── Phase 1: insert Pending record before mutation ────────────────────────

    /// <summary>
    /// Inserts a Pending audit record before the mutation executes.
    /// Returns the generated Id. Must complete before any SAP or MolasIntegration mutation.
    /// </summary>
    public virtual async Task<long> InsertAsync(ZfAdminAuditEntry entry, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO dbo.ZfAdminAuditLog
                (ActionId, SoDocEntry, RequestId, ActionType, ReasonCode,
                 RequestedBy, RequestedAtUtc, Result, BeforeStateJson, EvidenceJson)
            VALUES
                (@actionId, @soDocEntry, @requestId, @actionType, @reasonCode,
                 @requestedBy, @requestedAtUtc, @result, @beforeStateJson, @evidenceJson);
            SELECT SCOPE_IDENTITY();
            """;
        await using var conn = new SqlConnection(_cs);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@actionId",        entry.ActionId);
        cmd.Parameters.AddWithValue("@soDocEntry",      entry.SoDocEntry);
        cmd.Parameters.AddWithValue("@requestId",       (object?)entry.RequestId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@actionType",      entry.ActionType);
        cmd.Parameters.AddWithValue("@reasonCode",      (object?)entry.ReasonCode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@requestedBy",     entry.RequestedBy);
        cmd.Parameters.AddWithValue("@requestedAtUtc",  entry.RequestedAtUtc);
        cmd.Parameters.AddWithValue("@result",          entry.Result);
        cmd.Parameters.AddWithValue("@beforeStateJson", (object?)entry.BeforeStateJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@evidenceJson",    (object?)entry.EvidenceJson    ?? DBNull.Value);

        var scalar = await cmd.ExecuteScalarAsync(ct);
        var id = Convert.ToInt64(scalar);
        entry.Id = id;
        return id;
    }

    // ── Phase 2: update with result after mutation ────────────────────────────

    /// <summary>
    /// Sets the terminal result on an existing Pending audit record.
    /// Called after the mutation completes (success, failure, or recovery-required).
    /// </summary>
    public virtual async Task SetPostStateAsync(
        long      id,
        string    result,
        string?   afterStateJson,
        string?   errorCode,
        string?   errorMessage,
        DateTime  executedAtUtc,
        CancellationToken ct = default)
    {
        const string sql = """
            UPDATE dbo.ZfAdminAuditLog
            SET    Result         = @result,
                   AfterStateJson = @afterStateJson,
                   ErrorCode      = @errorCode,
                   ErrorMessage   = @errorMessage,
                   ExecutedAtUtc  = @executedAtUtc
            WHERE  Id = @id;
            """;
        await using var conn = new SqlConnection(_cs);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@id",             id);
        cmd.Parameters.AddWithValue("@result",         result);
        cmd.Parameters.AddWithValue("@afterStateJson", (object?)afterStateJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@errorCode",      (object?)errorCode      ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@errorMessage",   (object?)errorMessage   ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@executedAtUtc",  executedAtUtc);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── Query (admin GET /audit) ──────────────────────────────────────────────

    public async Task<List<ZfAdminAuditEntry>> GetByOrderAsync(
        int soDocEntry, int limit = 50, CancellationToken ct = default)
    {
        const string sql = """
            SELECT TOP (@limit)
                   Id, ActionId, SoDocEntry, RequestId, ActionType, ReasonCode,
                   RequestedBy, RequestedAtUtc, ExecutedAtUtc, Result,
                   BeforeStateJson, AfterStateJson, EvidenceJson, ErrorCode, ErrorMessage
            FROM   dbo.ZfAdminAuditLog
            WHERE  SoDocEntry = @soDocEntry
            ORDER  BY RequestedAtUtc DESC;
            """;
        await using var conn = new SqlConnection(_cs);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@limit",      limit);
        cmd.Parameters.AddWithValue("@soDocEntry", soDocEntry);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        var list = new List<ZfAdminAuditEntry>();
        while (await rdr.ReadAsync(ct))
            list.Add(ReadEntry(rdr));
        return list;
    }

    private static ZfAdminAuditEntry ReadEntry(SqlDataReader r) => new()
    {
        Id              = r.GetInt64(0),
        ActionId        = r.GetGuid(1),
        SoDocEntry      = r.GetInt32(2),
        RequestId       = r.IsDBNull(3)  ? null : r.GetGuid(3),
        ActionType      = r.GetString(4),
        ReasonCode      = r.IsDBNull(5)  ? null : r.GetString(5),
        RequestedBy     = r.GetString(6),
        RequestedAtUtc  = r.GetDateTime(7),
        ExecutedAtUtc   = r.IsDBNull(8)  ? null : r.GetDateTime(8),
        Result          = r.GetString(9),
        BeforeStateJson = r.IsDBNull(10) ? null : r.GetString(10),
        AfterStateJson  = r.IsDBNull(11) ? null : r.GetString(11),
        EvidenceJson    = r.IsDBNull(12) ? null : r.GetString(12),
        ErrorCode       = r.IsDBNull(13) ? null : r.GetString(13),
        ErrorMessage    = r.IsDBNull(14) ? null : r.GetString(14),
    };

    // ── Snapshot helpers ─────────────────────────────────────────────────────

    public static string? SerializeSnapshot(ZfOrderDiagnosticResult? diag)
    {
        if (diag is null) return null;
        try
        {
            return JsonSerializer.Serialize(new
            {
                diag.OrchestrationId, diag.RequestId, diag.SoDocEntry,
                diag.State, diag.UpdatedAtUtc,
                Consistency = new { diag.Consistency.Status, diag.Consistency.Detail },
                DeliveryCount = diag.Deliveries.Count,
                FragmentCount = diag.Fragments.Count,
            });
        }
        catch { return null; }
    }
}
