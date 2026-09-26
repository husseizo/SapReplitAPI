using Microsoft.Data.SqlClient;
using SapReplitAPI.Models.ZoneFulfillment;

namespace SapReplitAPI.Services.ZoneFulfillment;

/// <summary>
/// The single authoritative "what ZF_FRAGMENT_RDR1_MISSING incidents exist right now"
/// query. Pure T-SQL cross-database join (MolasIntegration.dbo.SoLineFragment/
/// FulfillmentOrchestration LEFT JOIN MOLAS_Live_2021.dbo.RDR1) — no SAP DI API/COM call,
/// so no concurrency gate is needed here; SQL Server serializes its own readers.
///
/// This is the ONLY place the IncidentKey format is built in SQL — extracted from
/// ZfDashboardService so it has exactly one implementation instead of two (the prior
/// version hand-duplicated the CONCAT(...) formula separately from
/// ZfIncidentResolutionRepository.BuildIncidentKey, a latent drift risk). Both
/// ZfDashboardService (read-only enrichment) and ZfIncidentObservationService
/// (authoritative technical-state writer) call this same method.
/// </summary>
public class ZfLiveIncidentDetector
{
    private readonly string _cs;

    public ZfLiveIncidentDetector(IConfiguration config)
    {
        _cs = config.GetConnectionString("MolasIntegration")
            ?? throw new InvalidOperationException("MolasIntegration connection string not configured.");
    }

    /// <summary>Protected constructor for test subclasses (in-memory fakes).</summary>
    protected ZfLiveIncidentDetector() => _cs = "";

    /// <summary>
    /// Returns every SoLineFragment currently missing its corresponding SAP RDR1 line.
    /// Read-only. Throws on failure — callers decide how to handle a failed scan
    /// (ZfIncidentObservationService: a failed scan must NEVER be treated as recovery).
    /// </summary>
    public virtual async Task<List<ZfLiveIncidentObservation>> DetectCurrentIncidentsAsync(CancellationToken ct = default)
    {
        const string sql = """
            SELECT
                CONCAT(CAST(o.SoDocNum AS NVARCHAR(20)), '_',
                       CAST(f.Id AS NVARCHAR(20)), '_ZF_FRAGMENT_RDR1_MISSING') AS IncidentKey,
                o.SoDocNum,
                o.SoDocEntry,
                o.Id                 AS OrchestrationId,
                f.Id                 AS FragmentId,
                f.SoLineNum,
                f.ItemCode,
                f.WhsCode,
                f.SoLineQty,
                o.State              AS OrchestrationState
            FROM       dbo.SoLineFragment           f
            INNER JOIN dbo.FulfillmentOrchestration o  ON o.Id = f.OrchestrationId
            LEFT  JOIN MOLAS_Live_2021.dbo.RDR1     sap ON sap.DocEntry = f.SoDocEntry
                                                       AND sap.LineNum  = f.SoLineNum
            WHERE sap.DocEntry IS NULL;
            """;

        await using var conn = new SqlConnection(_cs);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);

        var result = new List<ZfLiveIncidentObservation>();
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            result.Add(new ZfLiveIncidentObservation
            {
                IncidentKey        = rdr.GetString(0),
                SoDocNum           = rdr.GetInt32(1),
                SoDocEntry         = rdr.IsDBNull(2) ? null : rdr.GetInt32(2),
                OrchestrationId    = rdr.GetInt64(3),
                FragmentId         = rdr.GetInt64(4),
                SoLineNum          = rdr.GetInt32(5),
                ItemCode           = rdr.GetString(6),
                WhsCode            = rdr.GetString(7),
                SoLineQty          = rdr.GetDecimal(8),
                OrchestrationState = rdr.GetString(9),
                IncidentCode       = ZfConsistencyStatus.FragmentRdr1Missing,
                Severity           = ZfDiagnosticSeverity.High,
            });
        }
        return result;
    }
}
