using Microsoft.Data.SqlClient;
using SapReplitAPI.Models.ZoneFulfillment;

namespace SapReplitAPI.Services.ZoneFulfillment;

/// <summary>
/// Durable technical incident lifecycle history — dbo.ZfDiagnosticIncidentHistory on
/// MolasIntegration (SQL Server). Same storage/registration convention as
/// ZfIncidentResolutionRepository and ZfAdminAuditRepository (raw ADO.NET, Singleton,
/// self-provisioning EnsureTableAsync as a dev/first-deploy safety net — production
/// provisioning is the DBA script, see Scripts/ZfDiagnosticIncidentHistory_dba.sql).
///
/// Safety contract:
///   - No SAP calls of any kind — this repository only ever reads/writes its own table.
///   - The authoritative observation service (ZfIncidentObservationService) is the ONLY
///     writer. Nothing else may INSERT/UPDATE this table.
///   - Completely independent of dbo.ZfIncidentResolutions — joined only by IncidentKey,
///     read-only, at the dashboard layer.
/// </summary>
public class ZfDiagnosticIncidentHistoryRepository
{
    private readonly string _cs;
    private readonly ILogger<ZfDiagnosticIncidentHistoryRepository> _log;

    public ZfDiagnosticIncidentHistoryRepository(
        IConfiguration config,
        ILogger<ZfDiagnosticIncidentHistoryRepository> log)
    {
        _cs = config.GetConnectionString("MolasIntegration")
            ?? throw new InvalidOperationException("MolasIntegration connection string not configured.");
        _log = log;
    }

    /// <summary>Protected constructor for test subclasses (in-memory fakes). Never call from production code.</summary>
    protected ZfDiagnosticIncidentHistoryRepository()
    {
        _cs  = "";
        _log = Microsoft.Extensions.Logging.Abstractions.NullLogger<ZfDiagnosticIncidentHistoryRepository>.Instance;
    }

    // ── Schema bootstrap ──────────────────────────────────────────────────────

    /// <summary>
    /// Verifies dbo.ZfDiagnosticIncidentHistory exists. Dev/first-deploy safety net only —
    /// production provisioning is Scripts/ZfDiagnosticIncidentHistory_dba.sql (the runtime
    /// login is expected to have only SELECT/INSERT/UPDATE, not CREATE TABLE).
    /// </summary>
    public virtual async Task EnsureTableAsync(CancellationToken ct = default)
    {
        try
        {
            await using var conn = new SqlConnection(_cs);
            await conn.OpenAsync(ct);

            const string checkSql = """
                SELECT COUNT(1) FROM sys.tables
                WHERE  name = 'ZfDiagnosticIncidentHistory' AND schema_id = SCHEMA_ID('dbo');
                """;
            await using var checkCmd = new SqlCommand(checkSql, conn);
            var exists = Convert.ToInt32(await checkCmd.ExecuteScalarAsync(ct)) > 0;
            if (exists)
            {
                _log.LogInformation("✅ ZfDiagnosticIncidentHistoryRepository: dbo.ZfDiagnosticIncidentHistory verified.");
                return;
            }

            const string ddl = """
                CREATE TABLE dbo.ZfDiagnosticIncidentHistory (
                    Id                  BIGINT          IDENTITY(1,1) NOT NULL
                        CONSTRAINT PK_ZfDiagnosticIncidentHistory PRIMARY KEY,
                    IncidentKey         NVARCHAR(200)   NOT NULL,
                    OccurrenceNumber    INT             NOT NULL,
                    SoDocNum            INT             NOT NULL,
                    SoDocEntry          INT             NULL,
                    OrchestrationId     BIGINT          NULL,
                    FragmentId          BIGINT          NULL,
                    IncidentCode        NVARCHAR(100)   NOT NULL,
                    Severity            NVARCHAR(20)    NOT NULL,
                    ItemCode            NVARCHAR(50)    NULL,
                    ExpectedLineNum     INT             NULL,
                    ExpectedWhsCode     NVARCHAR(20)    NULL,
                    ExpectedQty         DECIMAL(18,4)   NULL,
                    LifecycleStatus     NVARCHAR(20)    NOT NULL,
                    FirstDetectedAtUtc  DATETIME2       NOT NULL,
                    LastObservedAtUtc   DATETIME2       NOT NULL,
                    ClearedAtUtc        DATETIME2       NULL,
                    RecoveryReason      NVARCHAR(200)   NULL,
                    DetectionSource     NVARCHAR(60)    NOT NULL,
                    EvidenceJson        NVARCHAR(MAX)   NULL,
                    CreatedAtUtc        DATETIME2       NOT NULL,
                    UpdatedAtUtc        DATETIME2       NOT NULL
                );
                CREATE UNIQUE INDEX UX_ZfDiagnosticIncidentHistory_Key_Occurrence
                    ON dbo.ZfDiagnosticIncidentHistory (IncidentKey, OccurrenceNumber);
                CREATE INDEX IX_ZfDiagnosticIncidentHistory_LifecycleStatus
                    ON dbo.ZfDiagnosticIncidentHistory (LifecycleStatus);
                CREATE INDEX IX_ZfDiagnosticIncidentHistory_ClearedAtUtc
                    ON dbo.ZfDiagnosticIncidentHistory (ClearedAtUtc);
                CREATE INDEX IX_ZfDiagnosticIncidentHistory_FirstDetectedAtUtc
                    ON dbo.ZfDiagnosticIncidentHistory (FirstDetectedAtUtc);
                CREATE INDEX IX_ZfDiagnosticIncidentHistory_SoDocNum
                    ON dbo.ZfDiagnosticIncidentHistory (SoDocNum);
                CREATE INDEX IX_ZfDiagnosticIncidentHistory_FragmentId
                    ON dbo.ZfDiagnosticIncidentHistory (FragmentId);
                CREATE INDEX IX_ZfDiagnosticIncidentHistory_OrchestrationId
                    ON dbo.ZfDiagnosticIncidentHistory (OrchestrationId);
                """;
            await using var ddlCmd = new SqlCommand(ddl, conn);
            await ddlCmd.ExecuteNonQueryAsync(ct);
            _log.LogInformation("✅ ZfDiagnosticIncidentHistoryRepository: dbo.ZfDiagnosticIncidentHistory created.");
        }
        catch (SqlException ex) when (ex.Number == 262)
        {
            _log.LogWarning(
                "ZfDiagnosticIncidentHistoryRepository: CREATE TABLE permission denied. " +
                "Run Scripts/ZfDiagnosticIncidentHistory_dba.sql with a DBA account, then: " +
                "GRANT SELECT, INSERT, UPDATE ON dbo.ZfDiagnosticIncidentHistory TO SapReplitOutboxApp;");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "ZfDiagnosticIncidentHistoryRepository: EnsureTableAsync failed (non-fatal).");
        }
    }

    // ── Write (observation service only) ─────────────────────────────────────

    /// <summary>Inserts a brand-new occurrence (OccurrenceNumber = prior max + 1, or 1 if none exist).</summary>
    public virtual async Task<long> InsertNewOccurrenceAsync(
        ZfDiagnosticIncidentHistoryRecord rec, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO dbo.ZfDiagnosticIncidentHistory
                (IncidentKey, OccurrenceNumber, SoDocNum, SoDocEntry, OrchestrationId, FragmentId,
                 IncidentCode, Severity, ItemCode, ExpectedLineNum, ExpectedWhsCode, ExpectedQty,
                 LifecycleStatus, FirstDetectedAtUtc, LastObservedAtUtc, ClearedAtUtc, RecoveryReason,
                 DetectionSource, EvidenceJson, CreatedAtUtc, UpdatedAtUtc)
            VALUES
                (@key, @occ, @docNum, @docEntry, @orchId, @fragId,
                 @code, @sev, @item, @lineNum, @whs, @qty,
                 @status, @firstDet, @lastObs, @cleared, @recReason,
                 @src, @evidence, @created, @updated);
            SELECT SCOPE_IDENTITY();
            """;

        await using var conn = new SqlConnection(_cs);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        BindCommon(cmd, rec);

        var result = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt64(result);
    }

    /// <summary>Updates LastObservedAtUtc + EvidenceJson on the current ACTIVE occurrence row.</summary>
    public virtual async Task UpdateObservedAsync(
        long id, DateTime lastObservedAtUtc, string? evidenceJson, DateTime updatedAtUtc, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE dbo.ZfDiagnosticIncidentHistory
            SET    LastObservedAtUtc = @lastObs, EvidenceJson = @evidence, UpdatedAtUtc = @updated
            WHERE  Id = @id;
            """;

        await using var conn = new SqlConnection(_cs);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@id",       id);
        cmd.Parameters.AddWithValue("@lastObs",  lastObservedAtUtc);
        cmd.Parameters.AddWithValue("@evidence", (object?)evidenceJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@updated",  updatedAtUtc);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Marks an ACTIVE occurrence RECOVERED. Only ever called after a SUCCESSFUL full
    /// observation confirmed the incident is no longer present — see ZfIncidentObservationService
    /// for the failure-safety contract (a failed/partial scan must never reach this method).
    /// </summary>
    public virtual async Task MarkRecoveredAsync(
        long id, DateTime clearedAtUtc, string recoveryReason, DateTime updatedAtUtc, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE dbo.ZfDiagnosticIncidentHistory
            SET    LifecycleStatus = @status, ClearedAtUtc = @cleared, RecoveryReason = @reason, UpdatedAtUtc = @updated
            WHERE  Id = @id AND LifecycleStatus = @activeStatus;
            """;

        await using var conn = new SqlConnection(_cs);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@id",           id);
        cmd.Parameters.AddWithValue("@status",       ZfLifecycleStatus.Recovered);
        cmd.Parameters.AddWithValue("@cleared",      clearedAtUtc);
        cmd.Parameters.AddWithValue("@reason",       recoveryReason);
        cmd.Parameters.AddWithValue("@updated",      updatedAtUtc);
        cmd.Parameters.AddWithValue("@activeStatus", ZfLifecycleStatus.Active);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── Read ──────────────────────────────────────────────────────────────────

    /// <summary>Returns the current ACTIVE occurrence (if any) for every IncidentKey — one row per key.</summary>
    public virtual async Task<IReadOnlyDictionary<string, ZfDiagnosticIncidentHistoryRecord>> GetActiveOccurrencesAsync(
        CancellationToken ct = default)
    {
        const string sql = """
            SELECT Id, IncidentKey, OccurrenceNumber, SoDocNum, SoDocEntry, OrchestrationId, FragmentId,
                   IncidentCode, Severity, ItemCode, ExpectedLineNum, ExpectedWhsCode, ExpectedQty,
                   LifecycleStatus, FirstDetectedAtUtc, LastObservedAtUtc, ClearedAtUtc, RecoveryReason,
                   DetectionSource, EvidenceJson, CreatedAtUtc, UpdatedAtUtc
            FROM   dbo.ZfDiagnosticIncidentHistory
            WHERE  LifecycleStatus = @activeStatus;
            """;

        await using var conn = new SqlConnection(_cs);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@activeStatus", ZfLifecycleStatus.Active);

        var result = new Dictionary<string, ZfDiagnosticIncidentHistoryRecord>(StringComparer.Ordinal);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            var rec = ReadRecord(rdr);
            result[rec.IncidentKey] = rec; // ACTIVE is unique per key by construction
        }
        return result;
    }

    /// <summary>
    /// Returns the latest occurrence (ACTIVE or RECOVERED) per IncidentKey across all incidents —
    /// this is the dashboard's technical-lifecycle source of truth.
    /// </summary>
    public virtual async Task<IReadOnlyList<ZfDiagnosticIncidentHistoryRecord>> GetLatestOccurrencesAsync(
        CancellationToken ct = default)
    {
        const string sql = """
            WITH Ranked AS (
                SELECT *, ROW_NUMBER() OVER (PARTITION BY IncidentKey ORDER BY OccurrenceNumber DESC) AS rn
                FROM   dbo.ZfDiagnosticIncidentHistory
            )
            SELECT Id, IncidentKey, OccurrenceNumber, SoDocNum, SoDocEntry, OrchestrationId, FragmentId,
                   IncidentCode, Severity, ItemCode, ExpectedLineNum, ExpectedWhsCode, ExpectedQty,
                   LifecycleStatus, FirstDetectedAtUtc, LastObservedAtUtc, ClearedAtUtc, RecoveryReason,
                   DetectionSource, EvidenceJson, CreatedAtUtc, UpdatedAtUtc
            FROM   Ranked
            WHERE  rn = 1;
            """;

        await using var conn = new SqlConnection(_cs);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);

        var result = new List<ZfDiagnosticIncidentHistoryRecord>();
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
            result.Add(ReadRecord(rdr));
        return result;
    }

    /// <summary>
    /// Counts occurrences whose ClearedAtUtc falls within [windowStartUtc, windowEndUtc) —
    /// the caller is responsible for computing that window in the correct operational
    /// timezone (see ZfDashboardService for the EAT "today" boundary).
    /// </summary>
    public virtual async Task<int> CountRecoveredInWindowAsync(
        DateTime windowStartUtc, DateTime windowEndUtc, CancellationToken ct = default)
    {
        const string sql = """
            SELECT COUNT(1)
            FROM   dbo.ZfDiagnosticIncidentHistory
            WHERE  LifecycleStatus = @recoveredStatus
              AND  ClearedAtUtc >= @start AND ClearedAtUtc < @end;
            """;

        await using var conn = new SqlConnection(_cs);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@recoveredStatus", ZfLifecycleStatus.Recovered);
        cmd.Parameters.AddWithValue("@start", windowStartUtc);
        cmd.Parameters.AddWithValue("@end",   windowEndUtc);

        var result = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt32(result);
    }

    /// <summary>Returns every occurrence (all statuses, all history) for one IncidentKey, oldest first.</summary>
    public virtual async Task<IReadOnlyList<ZfDiagnosticIncidentHistoryRecord>> GetOccurrencesAsync(
        string incidentKey, CancellationToken ct = default)
    {
        const string sql = """
            SELECT Id, IncidentKey, OccurrenceNumber, SoDocNum, SoDocEntry, OrchestrationId, FragmentId,
                   IncidentCode, Severity, ItemCode, ExpectedLineNum, ExpectedWhsCode, ExpectedQty,
                   LifecycleStatus, FirstDetectedAtUtc, LastObservedAtUtc, ClearedAtUtc, RecoveryReason,
                   DetectionSource, EvidenceJson, CreatedAtUtc, UpdatedAtUtc
            FROM   dbo.ZfDiagnosticIncidentHistory
            WHERE  IncidentKey = @key
            ORDER  BY OccurrenceNumber ASC;
            """;

        await using var conn = new SqlConnection(_cs);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@key", incidentKey);

        var result = new List<ZfDiagnosticIncidentHistoryRecord>();
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
            result.Add(ReadRecord(rdr));
        return result;
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static void BindCommon(SqlCommand cmd, ZfDiagnosticIncidentHistoryRecord rec)
    {
        cmd.Parameters.AddWithValue("@key",      rec.IncidentKey);
        cmd.Parameters.AddWithValue("@occ",      rec.OccurrenceNumber);
        cmd.Parameters.AddWithValue("@docNum",   rec.SoDocNum);
        cmd.Parameters.AddWithValue("@docEntry", (object?)rec.SoDocEntry ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@orchId",   (object?)rec.OrchestrationId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@fragId",   (object?)rec.FragmentId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@code",     rec.IncidentCode);
        cmd.Parameters.AddWithValue("@sev",      rec.Severity);
        cmd.Parameters.AddWithValue("@item",     (object?)rec.ItemCode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@lineNum",  (object?)rec.ExpectedLineNum ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@whs",      (object?)rec.ExpectedWhsCode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@qty",      (object?)rec.ExpectedQty ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@status",   rec.LifecycleStatus);
        cmd.Parameters.AddWithValue("@firstDet", rec.FirstDetectedAtUtc);
        cmd.Parameters.AddWithValue("@lastObs",  rec.LastObservedAtUtc);
        cmd.Parameters.AddWithValue("@cleared",  (object?)rec.ClearedAtUtc ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@recReason",(object?)rec.RecoveryReason ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@src",      rec.DetectionSource);
        cmd.Parameters.AddWithValue("@evidence", (object?)rec.EvidenceJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@created",  rec.CreatedAtUtc);
        cmd.Parameters.AddWithValue("@updated",  rec.UpdatedAtUtc);
    }

    private static ZfDiagnosticIncidentHistoryRecord ReadRecord(SqlDataReader rdr) => new()
    {
        Id                 = rdr.GetInt64(0),
        IncidentKey        = rdr.GetString(1),
        OccurrenceNumber   = rdr.GetInt32(2),
        SoDocNum           = rdr.GetInt32(3),
        SoDocEntry         = rdr.IsDBNull(4)  ? null : rdr.GetInt32(4),
        OrchestrationId    = rdr.IsDBNull(5)  ? null : rdr.GetInt64(5),
        FragmentId         = rdr.IsDBNull(6)  ? null : rdr.GetInt64(6),
        IncidentCode       = rdr.GetString(7),
        Severity           = rdr.GetString(8),
        ItemCode           = rdr.IsDBNull(9)  ? null : rdr.GetString(9),
        ExpectedLineNum    = rdr.IsDBNull(10) ? null : rdr.GetInt32(10),
        ExpectedWhsCode    = rdr.IsDBNull(11) ? null : rdr.GetString(11),
        ExpectedQty        = rdr.IsDBNull(12) ? null : rdr.GetDecimal(12),
        LifecycleStatus    = rdr.GetString(13),
        FirstDetectedAtUtc = rdr.GetDateTime(14),
        LastObservedAtUtc  = rdr.GetDateTime(15),
        ClearedAtUtc       = rdr.IsDBNull(16) ? null : rdr.GetDateTime(16),
        RecoveryReason     = rdr.IsDBNull(17) ? null : rdr.GetString(17),
        DetectionSource    = rdr.GetString(18),
        EvidenceJson       = rdr.IsDBNull(19) ? null : rdr.GetString(19),
        CreatedAtUtc       = rdr.GetDateTime(20),
        UpdatedAtUtc       = rdr.GetDateTime(21),
    };
}
