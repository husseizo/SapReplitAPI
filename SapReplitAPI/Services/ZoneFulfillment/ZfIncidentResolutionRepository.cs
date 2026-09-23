using Microsoft.Data.SqlClient;
using SapReplitAPI.Models.ZoneFulfillment;
using System.Text.Json;

namespace SapReplitAPI.Services.ZoneFulfillment;

/// <summary>
/// Append-only repository for ZF incident resolution history.
/// Uses raw ADO.NET against dbo.ZfIncidentResolutions on MolasIntegration (SQL Server).
/// Registered Singleton — creates its own SqlConnection per method call.
///
/// Safety contract:
///   - No UPDATE or DELETE operations; INSERT-only.
///   - No SAP mutations of any kind.
///   - MutationAvailable=false is enforced at the controller layer.
///
/// Runtime login requires only SELECT, INSERT — NOT CREATE TABLE.
/// To provision the table: run Scripts/ZfIncidentResolutions_dba.sql with a DBA account,
/// then GRANT SELECT, INSERT ON dbo.ZfIncidentResolutions TO SapReplitOutboxApp;
/// To add idempotency column: run Scripts/ZfIncidentResolutions_AddRequestId_dba.sql.
/// </summary>
public class ZfIncidentResolutionRepository
{
    private readonly string _cs;
    private readonly ILogger<ZfIncidentResolutionRepository> _log;

    public ZfIncidentResolutionRepository(
        IConfiguration config,
        ILogger<ZfIncidentResolutionRepository> log)
    {
        _cs = config.GetConnectionString("MolasIntegration")
            ?? throw new InvalidOperationException("MolasIntegration connection string not configured.");
        _log = log;
    }

    /// <summary>
    /// Protected constructor for test subclasses (FakeZfIncidentResolutionRepository).
    /// Subclasses override all virtual methods with in-memory implementations.
    /// Never call from production code.
    /// </summary>
    protected ZfIncidentResolutionRepository()
    {
        _cs  = "";
        _log = Microsoft.Extensions.Logging.Abstractions.NullLogger<ZfIncidentResolutionRepository>.Instance;
    }

    // ── Schema bootstrap ──────────────────────────────────────────────────────

    /// <summary>
    /// Checks whether the ResolutionRequestId column exists and logs a warning if absent.
    /// The column must be added by a DBA using Scripts/ZfIncidentResolutions_AddRequestId_dba.sql.
    /// Idempotency falls back to append-only if the column is missing.
    /// </summary>
    public virtual async Task EnsureResolutionRequestIdColumnAsync(CancellationToken ct = default)
    {
        try
        {
            await using var conn = new SqlConnection(_cs);
            await conn.OpenAsync(ct);

            const string checkSql = """
                SELECT COUNT(1) FROM INFORMATION_SCHEMA.COLUMNS
                WHERE TABLE_NAME = 'ZfIncidentResolutions' AND COLUMN_NAME = 'ResolutionRequestId';
                """;
            await using var cmd = new SqlCommand(checkSql, conn);
            var exists = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) > 0;
            if (exists)
            {
                _log.LogInformation("✅ ZfIncidentResolutionRepository: ResolutionRequestId column verified.");
            }
            else
            {
                _log.LogWarning(
                    "ZfIncidentResolutionRepository: ResolutionRequestId column absent. " +
                    "Run Scripts/ZfIncidentResolutions_AddRequestId_dba.sql to add it. " +
                    "Resolution idempotency will fall back to append-only until the column is added.");
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "ZfIncidentResolutionRepository: EnsureResolutionRequestIdColumnAsync failed (non-fatal).");
        }
    }

    /// <summary>
    /// Verifies the dbo.ZfIncidentResolutions table exists. If not, attempts to create it.
    /// In production the table is provisioned by the DBA script; this method is a safety net
    /// for first-deploy and dev environments. Logs a warning and continues if creation fails.
    /// </summary>
    public virtual async Task EnsureTableAsync(CancellationToken ct = default)
    {
        try
        {
            await using var conn = new SqlConnection(_cs);
            await conn.OpenAsync(ct);

            const string checkSql = """
                SELECT COUNT(1) FROM sys.tables
                WHERE  name = 'ZfIncidentResolutions' AND schema_id = SCHEMA_ID('dbo');
                """;
            await using var checkCmd = new SqlCommand(checkSql, conn);
            var exists = Convert.ToInt32(await checkCmd.ExecuteScalarAsync(ct)) > 0;
            if (exists)
            {
                _log.LogInformation("✅ ZfIncidentResolutionRepository: dbo.ZfIncidentResolutions verified.");
                return;
            }

            const string ddl = """
                CREATE TABLE dbo.ZfIncidentResolutions (
                    Id              BIGINT          IDENTITY(1,1) NOT NULL
                        CONSTRAINT PK_ZfIncidentResolutions PRIMARY KEY,
                    IncidentKey     NVARCHAR(200)   NOT NULL,
                    SoDocNum        INT             NOT NULL,
                    SoDocEntry      INT             NULL,
                    OrchestrationId BIGINT          NULL,
                    FragmentId      BIGINT          NULL,
                    IncidentCode    NVARCHAR(100)   NOT NULL,
                    Resolution      NVARCHAR(60)    NOT NULL,
                    Status          NVARCHAR(40)    NOT NULL,
                    Operator        NVARCHAR(200)   NOT NULL,
                    Reason          NVARCHAR(2000)  NOT NULL,
                    ResolvedAtUtc   DATETIME2       NOT NULL,
                    EvidenceJson    NVARCHAR(MAX)   NULL
                );
                CREATE INDEX IX_ZfIncidentResolutions_IncidentKey
                    ON dbo.ZfIncidentResolutions (IncidentKey, ResolvedAtUtc ASC);
                CREATE INDEX IX_ZfIncidentResolutions_SoDocNum
                    ON dbo.ZfIncidentResolutions (SoDocNum);
                """;
            await using var ddlCmd = new SqlCommand(ddl, conn);
            await ddlCmd.ExecuteNonQueryAsync(ct);
            _log.LogInformation("✅ ZfIncidentResolutionRepository: dbo.ZfIncidentResolutions created.");
        }
        catch (SqlException ex) when (ex.Number == 262)
        {
            _log.LogWarning(
                "ZfIncidentResolutionRepository: CREATE TABLE permission denied. " +
                "Run Scripts/ZfIncidentResolutions_dba.sql with a DBA account, then: " +
                "GRANT SELECT, INSERT ON dbo.ZfIncidentResolutions TO SapReplitOutboxApp;");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "ZfIncidentResolutionRepository: EnsureTableAsync failed (non-fatal).");
        }
    }

    // ── Write ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Inserts a resolution record.  Returns the new auto-incremented Id.
    /// This is append-only — no existing records are modified.
    /// When rec.ResolutionRequestId is set, the value is stored for idempotency checks.
    /// </summary>
    public virtual async Task<long> InsertResolutionAsync(
        ZfIncidentResolutionRecord rec, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO dbo.ZfIncidentResolutions
                (IncidentKey, SoDocNum, SoDocEntry, OrchestrationId, FragmentId, IncidentCode,
                 Resolution, Status, Operator, Reason, ResolvedAtUtc, EvidenceJson, ResolutionRequestId)
            VALUES
                (@key, @docNum, @docEntry, @orchId, @fragId, @code,
                 @resolution, @status, @operator, @reason, @ts, @evidence, @reqId);
            SELECT SCOPE_IDENTITY();
            """;

        await using var conn = new SqlConnection(_cs);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);

        cmd.Parameters.AddWithValue("@key",        rec.IncidentKey);
        cmd.Parameters.AddWithValue("@docNum",      rec.SoDocNum);
        cmd.Parameters.AddWithValue("@docEntry",    (object?)rec.SoDocEntry   ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@orchId",      (object?)rec.OrchestrationId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@fragId",      (object?)rec.FragmentId   ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@code",        rec.IncidentCode);
        cmd.Parameters.AddWithValue("@resolution",  rec.Resolution);
        cmd.Parameters.AddWithValue("@status",      rec.Status);
        cmd.Parameters.AddWithValue("@operator",    rec.Operator);
        cmd.Parameters.AddWithValue("@reason",      rec.Reason);
        cmd.Parameters.AddWithValue("@ts",          rec.ResolvedAtUtc);
        cmd.Parameters.AddWithValue("@evidence",    (object?)rec.EvidenceJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@reqId",       (object?)rec.ResolutionRequestId ?? DBNull.Value);

        var result = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt64(result);
    }

    /// <summary>
    /// Idempotent insert: if a record with the same ResolutionRequestId already exists,
    /// returns it unchanged (isReplay=true).  Otherwise inserts and returns the new record.
    /// Falls back to append-only if ResolutionRequestId is null.
    /// </summary>
    public virtual async Task<(ZfIncidentResolutionRecord record, bool isReplay)> InsertIdempotentAsync(
        ZfIncidentResolutionRecord rec, CancellationToken ct = default)
    {
        if (rec.ResolutionRequestId is null)
        {
            var newId = await InsertResolutionAsync(rec, ct);
            rec.Id = newId;
            return (rec, false);
        }

        // Check for existing record with this ResolutionRequestId
        var existing = await FindByResolutionRequestIdAsync(rec.ResolutionRequestId.Value, ct);
        if (existing is not null)
        {
            _log.LogInformation(
                "[ZfIncident] Idempotent replay: ResolutionRequestId={Guid} already recorded as Id={Id}",
                rec.ResolutionRequestId, existing.Id);
            return (existing, true);
        }

        var id = await InsertResolutionAsync(rec, ct);
        rec.Id = id;
        return (rec, false);
    }

    /// <summary>
    /// Returns the existing resolution record for a given client GUID, or null if not found.
    /// </summary>
    public virtual async Task<ZfIncidentResolutionRecord?> FindByResolutionRequestIdAsync(
        Guid requestId, CancellationToken ct = default)
    {
        const string sql = """
            SELECT Id, ResolutionRequestId, IncidentKey, SoDocNum, SoDocEntry, OrchestrationId, FragmentId,
                   IncidentCode, Resolution, Status, Operator, Reason, ResolvedAtUtc, EvidenceJson
            FROM   dbo.ZfIncidentResolutions
            WHERE  ResolutionRequestId = @reqId;
            """;

        await using var conn = new SqlConnection(_cs);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@reqId", requestId);

        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        if (!await rdr.ReadAsync(ct)) return null;

        return ReadRecord(rdr);
    }

    // ── Read ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns all resolution records for the given incident key, oldest first.
    /// </summary>
    public virtual async Task<IReadOnlyList<ZfIncidentResolutionRecord>> GetResolutionsAsync(
        string incidentKey, CancellationToken ct = default)
    {
        const string sql = """
            SELECT Id, ResolutionRequestId, IncidentKey, SoDocNum, SoDocEntry, OrchestrationId, FragmentId,
                   IncidentCode, Resolution, Status, Operator, Reason, ResolvedAtUtc, EvidenceJson
            FROM   dbo.ZfIncidentResolutions
            WHERE  IncidentKey = @key
            ORDER  BY Id ASC;
            """;

        await using var conn = new SqlConnection(_cs);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@key", incidentKey);

        var result = new List<ZfIncidentResolutionRecord>();
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
            result.Add(ReadRecord(rdr));
        return result;
    }

    /// <summary>
    /// Returns the most recent Status for this incidentKey, or ACTIVE if no record found.
    /// </summary>
    public virtual async Task<string> GetCurrentStatusAsync(
        string incidentKey, CancellationToken ct = default)
    {
        const string sql = """
            SELECT TOP 1 Status
            FROM   dbo.ZfIncidentResolutions
            WHERE  IncidentKey = @key
            ORDER  BY Id DESC;
            """;

        await using var conn = new SqlConnection(_cs);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@key", incidentKey);

        var result = await cmd.ExecuteScalarAsync(ct);
        return result is string s ? s : ZfIncidentStatusValue.Active;
    }

    /// <summary>
    /// Returns the latest resolution record per incident key across ALL incidents.
    /// Used by the dashboard to enrich live-detected incidents with human resolution data.
    /// </summary>
    public virtual async Task<IReadOnlyDictionary<string, ZfIncidentResolutionRecord>> GetAllLatestResolutionsAsync(
        CancellationToken ct = default)
    {
        const string sql = """
            WITH Ranked AS (
                SELECT *, ROW_NUMBER() OVER (PARTITION BY IncidentKey ORDER BY Id DESC) AS rn
                FROM   dbo.ZfIncidentResolutions
            )
            SELECT Id, ResolutionRequestId, IncidentKey, SoDocNum, SoDocEntry, OrchestrationId, FragmentId,
                   IncidentCode, Resolution, Status, Operator, Reason, ResolvedAtUtc, EvidenceJson
            FROM   Ranked
            WHERE  rn = 1;
            """;

        await using var conn = new SqlConnection(_cs);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);

        var result = new Dictionary<string, ZfIncidentResolutionRecord>(StringComparer.Ordinal);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            var rec = ReadRecord(rdr);
            result[rec.IncidentKey] = rec;
        }
        return result;
    }

    /// <summary>
    /// Returns all resolution records that reference incident keys NOT currently in the live
    /// detection set — these are historically resolved incidents whose SAP line has been restored
    /// or whose orchestration reached a terminal state.
    /// </summary>
    public virtual async Task<IReadOnlyList<ZfIncidentResolutionRecord>> GetHistoricalIncidentKeysAsync(
        CancellationToken ct = default)
    {
        const string sql = """
            SELECT DISTINCT ON_KEY.IncidentKey, ON_KEY.SoDocNum, ON_KEY.SoDocEntry,
                   ON_KEY.OrchestrationId, ON_KEY.FragmentId, ON_KEY.IncidentCode,
                   ON_KEY.Id, ON_KEY.ResolutionRequestId, ON_KEY.Resolution, ON_KEY.Status,
                   ON_KEY.Operator, ON_KEY.Reason, ON_KEY.ResolvedAtUtc, ON_KEY.EvidenceJson
            FROM (
                SELECT *, ROW_NUMBER() OVER (PARTITION BY IncidentKey ORDER BY Id DESC) AS rn
                FROM   dbo.ZfIncidentResolutions
            ) ON_KEY
            WHERE ON_KEY.rn = 1;
            """;

        await using var conn = new SqlConnection(_cs);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);

        var result = new List<ZfIncidentResolutionRecord>();
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
            result.Add(ReadRecord(rdr));
        return result;
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static ZfIncidentResolutionRecord ReadRecord(SqlDataReader rdr) => new()
    {
        Id              = rdr.GetInt64(0),
        ResolutionRequestId = rdr.IsDBNull(1) ? null : rdr.GetGuid(1),
        IncidentKey     = rdr.GetString(2),
        SoDocNum        = rdr.GetInt32(3),
        SoDocEntry      = rdr.IsDBNull(4)  ? null : rdr.GetInt32(4),
        OrchestrationId = rdr.IsDBNull(5)  ? null : rdr.GetInt64(5),
        FragmentId      = rdr.IsDBNull(6)  ? null : rdr.GetInt64(6),
        IncidentCode    = rdr.GetString(7),
        Resolution      = rdr.GetString(8),
        Status          = rdr.GetString(9),
        Operator        = rdr.GetString(10),
        Reason          = rdr.GetString(11),
        ResolvedAtUtc   = rdr.GetDateTime(12),
        EvidenceJson    = rdr.IsDBNull(13) ? null : rdr.GetString(13),
    };

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a deterministic incident key from its components.
    /// Format: "{soDocNum}_{fragmentId}_{incidentCode}"
    /// Example: "28879_20092_ZF_FRAGMENT_RDR1_MISSING"
    /// </summary>
    public static string BuildIncidentKey(int soDocNum, long? fragmentId, string incidentCode)
        => $"{soDocNum}_{fragmentId?.ToString() ?? "0"}_{incidentCode}";

    /// <summary>Serializes a list of evidence strings to JSON for storage.</summary>
    public static string? SerializeEvidence(IReadOnlyList<string>? evidence)
        => evidence is null || evidence.Count == 0
            ? null
            : JsonSerializer.Serialize(evidence);
}
