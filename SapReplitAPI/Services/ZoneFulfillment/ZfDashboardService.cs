using Microsoft.Data.SqlClient;
using SapReplitAPI.Models.ZoneFulfillment;

namespace SapReplitAPI.Services.ZoneFulfillment;

/// <summary>
/// Phase 2 dashboard service.
/// Combines a live cross-database query (MolasIntegration JOIN MOLAS_Live_2021.RDR1) with
/// the ZfIncidentResolutions history to produce the unified incident list and summary.
///
/// Safety contract:
///   - Read-only: no UPDATE, DELETE, or SAP mutations of any kind.
///   - TechnicalStatus and ResolutionStatus are always kept as separate dimensions.
///   - SapReplitOutboxApp requires db_datareader on MOLAS_Live_2021 (already granted).
///
/// Performance boundary (Phase 2):
///   - Two SQL queries per list/summary request (live detection + latest resolutions).
///   - Zero SAP DI API calls — all data from SQL Server.
///   - Filtering, sorting, and pagination are performed in-memory after the two queries.
///   - pageSize max 200; default 50.
///   - This architecture is suitable for the current incident volume (tens of rows).
///   - Migration to SQL-side filtering/pagination is required if incident volume grows materially.
///   - Incident detection covers ZF_FRAGMENT_RDR1_MISSING only (Phase 2 scope).
///     Other incident codes (warehouse mismatch, etc.) require separate detection queries.
/// </summary>
public sealed class ZfDashboardService
{
    private readonly string                          _cs;
    private readonly ZfIncidentResolutionRepository _resolutionRepo;
    private readonly ILogger<ZfDashboardService>    _log;

    // Column ordinals for the live detection SELECT (0-based)
    private const int ColIncidentKey      = 0;
    private const int ColSoDocNum         = 1;
    private const int ColSoDocEntry       = 2;
    private const int ColOrchestrationId  = 3;
    private const int ColFragmentId       = 4;
    private const int ColSoLineNum        = 5;
    private const int ColItemCode         = 6;
    private const int ColWhsCode          = 7;
    private const int ColSoLineQty        = 8;
    private const int ColOrchState        = 9;
    private const int ColDetectedAtUtc    = 10;
    private const int ColTechnicalStatus  = 11;

    public ZfDashboardService(
        IConfiguration                   config,
        ZfIncidentResolutionRepository   resolutionRepo,
        ILogger<ZfDashboardService>      log)
    {
        _cs             = config.GetConnectionString("MolasIntegration")
            ?? throw new InvalidOperationException("MolasIntegration connection string not configured.");
        _resolutionRepo = resolutionRepo;
        _log            = log;
    }

    // ── Summary ───────────────────────────────────────────────────────────────

    public async Task<ZfDiagnosticSummaryDto> GetSummaryAsync(CancellationToken ct = default)
    {
        var (liveIncidents, _) = await GetAllIncidentsAsync(null, ct);
        var now = DateTime.UtcNow;
        var todayStart = now.Date;
        var weekStart  = todayStart.AddDays(-7);

        var dto = new ZfDiagnosticSummaryDto();

        foreach (var inc in liveIncidents)
        {
            if (inc.TechnicalStatus == ZfDiagnosticIncidentStatus.Active)
            {
                dto.ActiveIncidentCount++;

                if (inc.Severity == ZfDiagnosticSeverity.High)   dto.HighSeverityCount++;
                if (inc.Severity == ZfDiagnosticSeverity.Medium) dto.MediumSeverityCount++;

                // Unresolved = active technical AND no human resolution decision recorded
                if (inc.AgeHours >= 24 && inc.LatestResolution is null) dto.UnresolvedOver24h++;
            }

            if (inc.LatestResolutionAtUtc.HasValue && inc.LatestResolutionAtUtc.Value >= todayStart)
                dto.ResolvedToday++;

            if (inc.DetectedAtUtc >= todayStart)    dto.IncidentsToday++;
            if (inc.DetectedAtUtc >= weekStart)     dto.IncidentsLast7Days++;

            Increment(dto.IncidentsByCode,     inc.Code);
            Increment(dto.IncidentsByCategory, inc.Category);
            Increment(dto.IncidentsBySeverity, inc.Severity);
            Increment(dto.IncidentsByTechnicalStatus,  inc.TechnicalStatus);
            Increment(dto.IncidentsByResolutionStatus, inc.ResolutionStatus);
        }

        return dto;
    }

    // ── Incident list ──────────────────────────────────────────────────────────

    public async Task<ZfIncidentListResponse> GetIncidentListAsync(
        ZfIncidentListQuery query, CancellationToken ct = default)
    {
        var (allIncidents, _) = await GetAllIncidentsAsync(query, ct);

        // Apply filters
        var filtered = ApplyFilters(allIncidents, query);

        // Sort
        filtered = ApplySort(filtered, query);

        var total = filtered.Count;
        var page  = Math.Max(1, query.Page);
        var size  = Math.Clamp(query.PageSize, 1, 200);

        var paged = filtered
            .Skip((page - 1) * size)
            .Take(size)
            .ToList();

        return new ZfIncidentListResponse
        {
            TotalCount = total,
            Page       = page,
            PageSize   = size,
            HasMore    = page * size < total,
            Items      = paged,
        };
    }

    // ── Core data assembly ────────────────────────────────────────────────────

    /// <summary>
    /// Assembles the full unified incident list from:
    ///   1. Live cross-db detection (SoLineFragment LEFT JOIN MOLAS_Live_2021.RDR1)
    ///   2. Resolution history (enriches live detections + adds historical-only records)
    /// </summary>
    private async Task<(List<ZfIncidentListItem> incidents, DateTime asOf)> GetAllIncidentsAsync(
        ZfIncidentListQuery? query, CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        // 1. Live detections (all fragments whose SAP RDR1 line is currently absent)
        List<ZfIncidentListItem> liveDetections;
        try
        {
            liveDetections = await RunLiveDetectionQueryAsync(ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[ZfDashboard] Live detection query failed; dashboard may be incomplete.");
            liveDetections = [];
        }

        // 2. Latest resolution per incident key (from audit table)
        IReadOnlyDictionary<string, ZfIncidentResolutionRecord> resolutions;
        try
        {
            resolutions = await _resolutionRepo.GetAllLatestResolutionsAsync(ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[ZfDashboard] GetAllLatestResolutionsAsync failed; resolution status unavailable.");
            resolutions = new Dictionary<string, ZfIncidentResolutionRecord>();
        }

        // 3. Enrich live detections with resolution data
        var liveKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var inc in liveDetections)
        {
            liveKeys.Add(inc.IncidentKey);
            if (resolutions.TryGetValue(inc.IncidentKey, out var res))
            {
                inc.ResolutionStatus          = res.Status;
                inc.LatestResolution          = res.Resolution;
                inc.LatestResolutionAtUtc     = res.ResolvedAtUtc;
                inc.LatestResolutionOperator  = res.Operator;
            }
            else
            {
                inc.ResolutionStatus = ZfIncidentStatusValue.Active;
            }

            SetAging(inc, now);
        }

        // 4. Add historical-only incidents (SAP line restored or orch terminal, but resolution exists)
        foreach (var (key, res) in resolutions)
        {
            if (liveKeys.Contains(key)) continue;

            // Build from resolution record — SAP line no longer absent (recovered/historical)
            var hist = new ZfIncidentListItem
            {
                IncidentKey            = res.IncidentKey,
                SoDocNum               = res.SoDocNum,
                SoDocEntry             = res.SoDocEntry,
                OrchestrationId        = res.OrchestrationId,
                FragmentId             = res.FragmentId,
                ItemCode               = string.Empty,   // not stored in resolution record
                Code                   = res.IncidentCode,
                Category               = ZfDiagnosticCategory.SapZfIntegrityDivergence,
                Severity               = ZfDiagnosticSeverity.High,
                TechnicalStatus        = ZfDiagnosticIncidentStatus.Historical,
                ResolutionStatus       = res.Status,
                OrchestrationState     = string.Empty,
                DetectedAtUtc          = res.ResolvedAtUtc,   // best proxy when no detection record
                LastObservedAtUtc      = res.ResolvedAtUtc,
                LatestResolution          = res.Resolution,
                LatestResolutionAtUtc     = res.ResolvedAtUtc,
                LatestResolutionOperator  = res.Operator,
            };
            SetAging(hist, now);
            liveDetections.Add(hist);
        }

        return (liveDetections, now);
    }

    // ── Live detection SQL ─────────────────────────────────────────────────────

    private async Task<List<ZfIncidentListItem>> RunLiveDetectionQueryAsync(CancellationToken ct)
    {
        // Cross-database query: MolasIntegration fragments LEFT JOIN MOLAS_Live_2021 RDR1
        // Finds all fragments where the SAP Sales Order line is currently absent.
        // SapReplitOutboxApp requires db_datareader on MOLAS_Live_2021 (already granted).
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
                o.State              AS OrchestrationState,
                f.CreatedAtUtc       AS DetectedAtUtc,
                CASE WHEN o.State IN ('Canceled','Failed') THEN 'HISTORICAL' ELSE 'ACTIVE' END
                                     AS TechnicalStatus
            FROM       dbo.SoLineFragment           f
            INNER JOIN dbo.FulfillmentOrchestration o  ON o.Id = f.OrchestrationId
            LEFT  JOIN MOLAS_Live_2021.dbo.RDR1     sap ON sap.DocEntry = f.SoDocEntry
                                                       AND sap.LineNum  = f.SoLineNum
            WHERE sap.DocEntry IS NULL;
            """;

        await using var conn = new SqlConnection(_cs);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);

        var result = new List<ZfIncidentListItem>();
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            result.Add(new ZfIncidentListItem
            {
                IncidentKey        = rdr.GetString(ColIncidentKey),
                SoDocNum           = rdr.GetInt32(ColSoDocNum),
                SoDocEntry         = rdr.IsDBNull(ColSoDocEntry) ? null : rdr.GetInt32(ColSoDocEntry),
                OrchestrationId    = rdr.GetInt64(ColOrchestrationId),
                FragmentId         = rdr.GetInt64(ColFragmentId),
                SoLineNum          = rdr.GetInt32(ColSoLineNum),
                ItemCode           = rdr.GetString(ColItemCode),
                ExpectedWhsCode    = rdr.GetString(ColWhsCode),
                ExpectedQty        = rdr.GetDecimal(ColSoLineQty),
                Code               = ZfConsistencyStatus.FragmentRdr1Missing,
                Category           = ZfDiagnosticCategory.SapZfIntegrityDivergence,
                Severity           = ZfDiagnosticSeverity.High,
                TechnicalStatus    = rdr.GetString(ColTechnicalStatus),
                OrchestrationState = rdr.GetString(ColOrchState),
                DetectedAtUtc      = rdr.GetDateTime(ColDetectedAtUtc),
                LastObservedAtUtc  = DateTime.UtcNow,
            });
        }
        return result;
    }

    // ── Filters ───────────────────────────────────────────────────────────────

    private static List<ZfIncidentListItem> ApplyFilters(
        List<ZfIncidentListItem> items, ZfIncidentListQuery query)
    {
        IEnumerable<ZfIncidentListItem> q = items;

        if (!string.IsNullOrWhiteSpace(query.TechnicalStatus))
            q = q.Where(i => i.TechnicalStatus.Equals(query.TechnicalStatus, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(query.ResolutionStatus))
            q = q.Where(i => i.ResolutionStatus.Equals(query.ResolutionStatus, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(query.Severity))
            q = q.Where(i => i.Severity.Equals(query.Severity, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(query.IncidentCode))
            q = q.Where(i => i.Code.Equals(query.IncidentCode, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(query.Category))
            q = q.Where(i => i.Category.Equals(query.Category, StringComparison.OrdinalIgnoreCase));

        if (query.SoDocNum.HasValue)
            q = q.Where(i => i.SoDocNum == query.SoDocNum.Value);

        if (!string.IsNullOrWhiteSpace(query.ItemCode))
            q = q.Where(i => i.ItemCode.Contains(query.ItemCode, StringComparison.OrdinalIgnoreCase));

        if (query.FragmentId.HasValue)
            q = q.Where(i => i.FragmentId == query.FragmentId.Value);

        if (query.OrchestrationId.HasValue)
            q = q.Where(i => i.OrchestrationId == query.OrchestrationId.Value);

        if (query.DateFrom.HasValue)
            q = q.Where(i => i.DetectedAtUtc >= query.DateFrom.Value);

        if (query.DateTo.HasValue)
            q = q.Where(i => i.DetectedAtUtc <= query.DateTo.Value);

        if (query.HasResolution == true)
            q = q.Where(i => i.LatestResolution != null);

        if (query.HasResolution == false)
            q = q.Where(i => i.LatestResolution == null);

        if (!string.IsNullOrWhiteSpace(query.AgingBucket))
            q = q.Where(i => i.AgingBucket == query.AgingBucket);

        return q.ToList();
    }

    private static List<ZfIncidentListItem> ApplySort(
        List<ZfIncidentListItem> items, ZfIncidentListQuery query)
    {
        bool desc = !query.SortDir.Equals("asc", StringComparison.OrdinalIgnoreCase);

        // Secondary sort by IncidentKey (stable, deterministic) ensures consistent
        // pagination when the primary sort key has ties.
        return query.Sort.ToLowerInvariant() switch
        {
            "severity"         => desc ? items.OrderByDescending(i => i.Severity).ThenBy(i => i.IncidentKey).ToList()
                                       : items.OrderBy(i => i.Severity).ThenBy(i => i.IncidentKey).ToList(),
            "sodocnum"         => desc ? items.OrderByDescending(i => i.SoDocNum).ThenBy(i => i.IncidentKey).ToList()
                                       : items.OrderBy(i => i.SoDocNum).ThenBy(i => i.IncidentKey).ToList(),
            "agingbucket"      => desc ? items.OrderByDescending(i => i.AgeMinutes).ThenBy(i => i.IncidentKey).ToList()
                                       : items.OrderBy(i => i.AgeMinutes).ThenBy(i => i.IncidentKey).ToList(),
            "resolutionstatus" => desc ? items.OrderByDescending(i => i.ResolutionStatus).ThenBy(i => i.IncidentKey).ToList()
                                       : items.OrderBy(i => i.ResolutionStatus).ThenBy(i => i.IncidentKey).ToList(),
            _                  => desc ? items.OrderByDescending(i => i.DetectedAtUtc).ThenBy(i => i.IncidentKey).ToList()
                                       : items.OrderBy(i => i.DetectedAtUtc).ThenBy(i => i.IncidentKey).ToList(),
        };
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void SetAging(ZfIncidentListItem inc, DateTime now)
    {
        var diff = now - inc.DetectedAtUtc;
        inc.AgeMinutes  = diff.TotalMinutes;
        inc.AgeHours    = diff.TotalHours;
        inc.AgeDays     = diff.TotalDays;
        inc.AgingBucket = ZfAgingBucket.Classify(inc.AgeMinutes);
    }

    private static void Increment(Dictionary<string, int> dict, string key)
    {
        dict.TryGetValue(key, out var v);
        dict[key] = v + 1;
    }
}
