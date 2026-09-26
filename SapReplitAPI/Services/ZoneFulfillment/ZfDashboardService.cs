using SapReplitAPI.Models.ZoneFulfillment;

namespace SapReplitAPI.Services.ZoneFulfillment;

/// <summary>
/// Phase 2 dashboard service.
/// Combines durable technical lifecycle (dbo.ZfDiagnosticIncidentHistory, kept current by
/// ZfIncidentObservationJob — see that class) with the ZfIncidentResolutions human-decision
/// history to produce the unified incident list and summary.
///
/// Safety contract:
///   - Read-only: no UPDATE, DELETE, or SAP mutations of any kind. This service never
///     writes to ZfDiagnosticIncidentHistory — ZfIncidentObservationService is the only writer.
///   - TechnicalStatus and ResolutionStatus are always kept as separate dimensions.
///   - Dashboard requests do NOT trigger a live SAP/SQL detection scan — they only ever
///     read already-persisted state, so lifecycle accuracy does not depend on anyone
///     having the dashboard open (durable history is refreshed independently, on schedule).
///
/// Performance boundary (Phase 2):
///   - Two SQL queries per list/summary request (durable lifecycle + latest resolutions).
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
    private readonly ZfDiagnosticIncidentHistoryRepository _historyRepo;
    private readonly ZfIncidentResolutionRepository _resolutionRepo;
    private readonly ILogger<ZfDashboardService>    _log;

    // EAT has no DST, but resolve via TimeZoneInfo (not a hardcoded +3 offset) to match
    // the convention already used by other EAT-scheduled jobs in this codebase.
    private static readonly TimeZoneInfo EatZone =
        TimeZoneInfo.FindSystemTimeZoneById("E. Africa Standard Time");

    public ZfDashboardService(
        ZfDiagnosticIncidentHistoryRepository historyRepo,
        ZfIncidentResolutionRepository        resolutionRepo,
        ILogger<ZfDashboardService>           log)
    {
        _historyRepo    = historyRepo;
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

        // RecoveredToday: EAT operational day, not the server's UTC calendar boundary.
        // Production runs in Tanzania (EAT, UTC+3, no DST) — "today" for this tile means
        // 00:00–23:59 EAT, converted to the equivalent UTC window for the ClearedAtUtc query.
        var (eatTodayStartUtc, eatTodayEndUtc) = GetEatTodayWindowUtc(now);
        try
        {
            dto.RecoveredToday = await _historyRepo.CountRecoveredInWindowAsync(eatTodayStartUtc, eatTodayEndUtc, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[ZfDashboard] CountRecoveredInWindowAsync failed; RecoveredToday unavailable this request.");
        }

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
    ///   1. Durable technical lifecycle — latest occurrence per IncidentKey (ACTIVE or
    ///      RECOVERED), from dbo.ZfDiagnosticIncidentHistory. This is read-only here —
    ///      ZfIncidentObservationService is the sole writer, on its own schedule,
    ///      independent of dashboard usage.
    ///   2. Resolution history (enriches durable rows + adds legacy-only records for
    ///      resolutions with no matching durable row at all — e.g. resolved before this
    ///      table existed).
    /// </summary>
    private async Task<(List<ZfIncidentListItem> incidents, DateTime asOf)> GetAllIncidentsAsync(
        ZfIncidentListQuery? query, CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        // 1. Durable technical lifecycle (never a live SAP/SQL scan — always persisted state)
        IReadOnlyList<ZfDiagnosticIncidentHistoryRecord> lifecycleRows;
        try
        {
            lifecycleRows = await _historyRepo.GetLatestOccurrencesAsync(ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[ZfDashboard] GetLatestOccurrencesAsync failed; dashboard may be incomplete.");
            lifecycleRows = [];
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

        var items = new List<ZfIncidentListItem>(lifecycleRows.Count);
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);

        // 3. Durable rows, enriched with resolution data
        foreach (var row in lifecycleRows)
        {
            seenKeys.Add(row.IncidentKey);

            var inc = new ZfIncidentListItem
            {
                IncidentKey        = row.IncidentKey,
                SoDocNum           = row.SoDocNum,
                SoDocEntry         = row.SoDocEntry,
                OrchestrationId    = row.OrchestrationId,
                FragmentId         = row.FragmentId,
                SoLineNum          = row.ExpectedLineNum ?? 0,
                ItemCode           = row.ItemCode ?? string.Empty,
                ExpectedWhsCode    = row.ExpectedWhsCode ?? string.Empty,
                ExpectedQty        = row.ExpectedQty ?? 0m,
                Code               = row.IncidentCode,
                Category           = ZfDiagnosticCategory.SapZfIntegrityDivergence,
                Severity           = row.Severity,
                TechnicalStatus    = row.LifecycleStatus,       // ACTIVE | RECOVERED
                OrchestrationState = string.Empty,
                DetectedAtUtc      = row.FirstDetectedAtUtc,
                LastObservedAtUtc  = row.LastObservedAtUtc,
                ClearedAtUtc       = row.ClearedAtUtc,
            };

            if (resolutions.TryGetValue(row.IncidentKey, out var res))
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
            items.Add(inc);
        }

        // 4. Legacy-only incidents: a resolution exists but durable history has never seen
        //    this key at all (resolved before this table existed). TechnicalStatus stays
        //    HISTORICAL for these — see the doc comment on ZfIncidentListItem.TechnicalStatus.
        //    This is NOT a backfill: no timestamps are fabricated, the resolution's own
        //    ResolvedAtUtc is used as-is, exactly as before this change.
        foreach (var (key, res) in resolutions)
        {
            if (seenKeys.Contains(key)) continue;

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
            items.Add(hist);
        }

        return (items, now);
    }

    // ── EAT "today" window ──────────────────────────────────────────────────────

    /// <summary>
    /// Returns [startUtc, endUtc) for "today" in the East Africa Time operational day
    /// (production runs in Tanzania, UTC+3, no DST) — used only for RecoveredToday so the
    /// tile matches the business day operators actually experience, not a UTC calendar day
    /// that rolls over at 03:00 local time.
    /// </summary>
    internal static (DateTime startUtc, DateTime endUtc) GetEatTodayWindowUtc(DateTime utcNow)
    {
        var eatNow      = TimeZoneInfo.ConvertTimeFromUtc(utcNow, EatZone);
        var eatTodayStart = eatNow.Date;
        var startUtc    = TimeZoneInfo.ConvertTimeToUtc(eatTodayStart, EatZone);
        var endUtc      = TimeZoneInfo.ConvertTimeToUtc(eatTodayStart.AddDays(1), EatZone);
        return (startUtc, endUtc);
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
