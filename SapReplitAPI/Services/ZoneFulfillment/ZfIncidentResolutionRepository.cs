using Microsoft.Data.Sqlite;
using SapReplitAPI.Models.ZoneFulfillment;
using System.Text.Json;

namespace SapReplitAPI.Services.ZoneFulfillment;

/// <summary>
/// Append-only repository for ZF incident resolution history.
/// Uses raw ADO.NET against the local SQLite productcache.db.
/// Registered Singleton — creates its own SqliteConnection per method call.
///
/// Safety contract:
///   - No UPDATE or DELETE operations; INSERT-only.
///   - No SAP mutations of any kind.
///   - MutationAvailable=false is enforced at the controller layer.
/// </summary>
public class ZfIncidentResolutionRepository
{
    private readonly string _connStr;
    private readonly ILogger<ZfIncidentResolutionRepository> _log;

    public ZfIncidentResolutionRepository(
        IConfiguration config,
        ILogger<ZfIncidentResolutionRepository> log)
    {
        _connStr = config.GetConnectionString("CacheDB")
            ?? throw new InvalidOperationException("CacheDB connection string not configured.");
        _log = log;
    }

    /// <summary>
    /// Protected constructor for test subclasses (FakeZfIncidentResolutionRepository).
    /// Subclasses override all virtual methods with in-memory implementations.
    /// Never call from production code.
    /// </summary>
    protected ZfIncidentResolutionRepository()
    {
        _connStr = "Data Source=:memory:";
        _log     = Microsoft.Extensions.Logging.Abstractions.NullLogger<ZfIncidentResolutionRepository>.Instance;
    }

    // ── Schema bootstrap ──────────────────────────────────────────────────────

    /// <summary>
    /// Creates ZfIncidentResolutions table and index if they do not exist.
    /// Safe to call on every startup — fully idempotent.
    /// </summary>
    public virtual async Task EnsureTableAsync(CancellationToken ct = default)
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS ZfIncidentResolutions (
                Id              INTEGER PRIMARY KEY AUTOINCREMENT,
                IncidentKey     TEXT NOT NULL,
                SoDocNum        INTEGER NOT NULL,
                FragmentId      INTEGER,
                IncidentCode    TEXT NOT NULL,
                Resolution      TEXT NOT NULL,
                Status          TEXT NOT NULL,
                Operator        TEXT NOT NULL,
                Reason          TEXT NOT NULL,
                ResolvedAtUtc   TEXT NOT NULL,
                EvidenceJson    TEXT
            );
            CREATE INDEX IF NOT EXISTS IX_ZfIncidentResolutions_IncidentKey
                ON ZfIncidentResolutions (IncidentKey);
            CREATE INDEX IF NOT EXISTS IX_ZfIncidentResolutions_SoDocNum
                ON ZfIncidentResolutions (SoDocNum);
            """;

        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);

        _log.LogInformation("✅ ZfIncidentResolutionRepository: ZfIncidentResolutions table verified/created.");
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
            INSERT INTO ZfIncidentResolutions
                (IncidentKey, SoDocNum, FragmentId, IncidentCode,
                 Resolution, Status, Operator, Reason, ResolvedAtUtc, EvidenceJson)
            VALUES
                (@key, @docNum, @fragId, @code,
                 @resolution, @status, @operator, @reason, @ts, @evidence);
            SELECT last_insert_rowid();
            """;

        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;

        cmd.Parameters.AddWithValue("@key",        rec.IncidentKey);
        cmd.Parameters.AddWithValue("@docNum",      rec.SoDocNum);
        cmd.Parameters.AddWithValue("@fragId",      (object?)rec.FragmentId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@code",        rec.IncidentCode);
        cmd.Parameters.AddWithValue("@resolution",  rec.Resolution);
        cmd.Parameters.AddWithValue("@status",      rec.Status);
        cmd.Parameters.AddWithValue("@operator",    rec.Operator);
        cmd.Parameters.AddWithValue("@reason",      rec.Reason);
        cmd.Parameters.AddWithValue("@ts",          rec.ResolvedAtUtc.ToString("O"));
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
            SELECT Id, IncidentKey, SoDocNum, FragmentId, IncidentCode,
                   Resolution, Status, Operator, Reason, ResolvedAtUtc, EvidenceJson
            FROM   ZfIncidentResolutions
            WHERE  IncidentKey = @key
            ORDER  BY Id ASC;
            """;

        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@key", incidentKey);

        var result = new List<ZfIncidentResolutionRecord>();
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            var tsStr = rdr.GetString(9);
            result.Add(new ZfIncidentResolutionRecord
            {
                Id           = rdr.GetInt64(0),
                IncidentKey  = rdr.GetString(1),
                SoDocNum     = rdr.GetInt32(2),
                FragmentId   = rdr.IsDBNull(3) ? null : rdr.GetInt64(3),
                IncidentCode = rdr.GetString(4),
                Resolution   = rdr.GetString(5),
                Status       = rdr.GetString(6),
                Operator     = rdr.GetString(7),
                Reason       = rdr.GetString(8),
                ResolvedAtUtc = DateTime.Parse(tsStr,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind),
                EvidenceJson = rdr.IsDBNull(10) ? null : rdr.GetString(10),
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
            SELECT Status
            FROM   ZfIncidentResolutions
            WHERE  IncidentKey = @key
            ORDER  BY Id DESC
            LIMIT  1;
            """;

        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@key", incidentKey);

        var result = await cmd.ExecuteScalarAsync(ct);
        return result is string s ? s : ZfIncidentStatusValue.Active;
    }

    // ── Helper ────────────────────────────────────────────────────────────────

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
