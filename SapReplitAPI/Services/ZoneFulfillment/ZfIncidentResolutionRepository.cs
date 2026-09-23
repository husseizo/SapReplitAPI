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
/// then GRANT SELECT, INSERT ON dbo.ZfIncidentResolutions TO &lt;runtime-login&gt;;
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
    /// </summary>
    public virtual async Task<long> InsertResolutionAsync(
        ZfIncidentResolutionRecord rec, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO dbo.ZfIncidentResolutions
                (IncidentKey, SoDocNum, SoDocEntry, OrchestrationId, FragmentId, IncidentCode,
                 Resolution, Status, Operator, Reason, ResolvedAtUtc, EvidenceJson)
            VALUES
                (@key, @docNum, @docEntry, @orchId, @fragId, @code,
                 @resolution, @status, @operator, @reason, @ts, @evidence);
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

        var result = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt64(result);
    }

    // ── Read ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns all resolution records for the given incident key, oldest first.
    /// </summary>
    public virtual async Task<IReadOnlyList<ZfIncidentResolutionRecord>> GetResolutionsAsync(
        string incidentKey, CancellationToken ct = default)
    {
        const string sql = """
            SELECT Id, IncidentKey, SoDocNum, SoDocEntry, OrchestrationId, FragmentId, IncidentCode,
                   Resolution, Status, Operator, Reason, ResolvedAtUtc, EvidenceJson
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
        {
            result.Add(new ZfIncidentResolutionRecord
            {
                Id              = rdr.GetInt64(0),
                IncidentKey     = rdr.GetString(1),
                SoDocNum        = rdr.GetInt32(2),
                SoDocEntry      = rdr.IsDBNull(3)  ? null : rdr.GetInt32(3),
                OrchestrationId = rdr.IsDBNull(4)  ? null : rdr.GetInt64(4),
                FragmentId      = rdr.IsDBNull(5)  ? null : rdr.GetInt64(5),
                IncidentCode    = rdr.GetString(6),
                Resolution      = rdr.GetString(7),
                Status          = rdr.GetString(8),
                Operator        = rdr.GetString(9),
                Reason          = rdr.GetString(10),
                ResolvedAtUtc   = rdr.GetDateTime(11),
                EvidenceJson    = rdr.IsDBNull(12) ? null : rdr.GetString(12),
            });
        }
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
