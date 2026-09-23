namespace SapReplitAPI.Models.ZoneFulfillment;

// ── Aging buckets ────────────────────────────────────────────────────────────

public static class ZfAgingBucket
{
    public const string Under1H  = "<1h";
    public const string H1To4    = "1-4h";
    public const string H4To24   = "4-24h";
    public const string D1To3    = "1-3d";
    public const string Over3D   = ">3d";

    public static string Classify(double ageMinutes) => ageMinutes switch
    {
        < 60              => Under1H,
        < 240             => H1To4,
        < 1440            => H4To24,
        < 4320            => D1To3,
        _                 => Over3D,
    };
}

// ── Summary ──────────────────────────────────────────────────────────────────

/// <summary>
/// Aggregated counts returned by GET /api/zf-admin/diagnostics/summary.
/// TechnicalStatus and HumanResolutionStatus are kept as separate dimensions.
/// </summary>
public sealed class ZfDiagnosticSummaryDto
{
    // Technical detection counts
    public int ActiveIncidentCount    { get; set; }
    public int HighSeverityCount      { get; set; }
    public int MediumSeverityCount    { get; set; }
    public int UnresolvedOver24h      { get; set; }

    // Human resolution counts (from dbo.ZfIncidentResolutions, today UTC)
    public int ResolvedToday          { get; set; }
    public int RecoveredToday         { get; set; }

    // Detection volume
    public int IncidentsToday         { get; set; }
    public int IncidentsLast7Days     { get; set; }

    // Distribution (key=code/category/severity/status → count)
    public Dictionary<string, int> IncidentsByCode     { get; set; } = new();
    public Dictionary<string, int> IncidentsByCategory { get; set; } = new();
    public Dictionary<string, int> IncidentsBySeverity { get; set; } = new();

    // Two separate status dimensions
    public Dictionary<string, int> IncidentsByTechnicalStatus    { get; set; } = new();
    public Dictionary<string, int> IncidentsByResolutionStatus   { get; set; } = new();
}

// ── Incident list ─────────────────────────────────────────────────────────────

/// <summary>
/// A single row in the incident dashboard list.
/// TechnicalStatus and ResolutionStatus are ALWAYS kept as separate fields —
/// e.g. SO 28879: TechnicalStatus=ACTIVE, ResolutionStatus=RESOLVED.
/// </summary>
public sealed class ZfIncidentListItem
{
    public string IncidentKey             { get; set; } = string.Empty;
    public int    SoDocNum                { get; set; }
    public int?   SoDocEntry              { get; set; }
    public long?  OrchestrationId         { get; set; }
    public long?  FragmentId              { get; set; }
    public int    SoLineNum               { get; set; }
    public string ItemCode                { get; set; } = string.Empty;
    public string ExpectedWhsCode         { get; set; } = string.Empty;
    public decimal ExpectedQty            { get; set; }
    public string Code                    { get; set; } = string.Empty;
    public string Category                { get; set; } = string.Empty;
    public string Severity                { get; set; } = string.Empty;

    /// <summary>Live detection status: ACTIVE | HISTORICAL. Independent of human decisions.</summary>
    public string TechnicalStatus         { get; set; } = string.Empty;

    /// <summary>Human resolution status: ACTIVE | ACKNOWLEDGED | RESOLVED | DEFERRED.</summary>
    public string ResolutionStatus        { get; set; } = string.Empty;

    public string OrchestrationState      { get; set; } = string.Empty;
    public DateTime DetectedAtUtc         { get; set; }
    public DateTime LastObservedAtUtc     { get; set; }

    // Aging (server-calculated)
    public double   AgeMinutes            { get; set; }
    public double   AgeHours             { get; set; }
    public double   AgeDays              { get; set; }
    public string   AgingBucket          { get; set; } = string.Empty;

    // Latest human resolution (null when no resolution recorded)
    public string?  LatestResolution         { get; set; }
    public DateTime? LatestResolutionAtUtc   { get; set; }
    public string?  LatestResolutionOperator { get; set; }
}

/// <summary>Paged response for GET /api/zf-admin/diagnostics/incidents.</summary>
public sealed class ZfIncidentListResponse
{
    public int TotalCount         { get; set; }
    public int Page               { get; set; }
    public int PageSize           { get; set; }
    public bool HasMore           { get; set; }
    public List<ZfIncidentListItem> Items { get; set; } = new();
}

// ── Query / filter ────────────────────────────────────────────────────────────

/// <summary>
/// Server-side filter parameters for GET /api/zf-admin/diagnostics/incidents.
/// All parameters are optional; absent parameters do not filter.
/// </summary>
public sealed class ZfIncidentListQuery
{
    /// <summary>Filter by TechnicalStatus: ACTIVE | HISTORICAL</summary>
    public string?  TechnicalStatus   { get; set; }

    /// <summary>Filter by ResolutionStatus: ACTIVE | ACKNOWLEDGED | RESOLVED | DEFERRED</summary>
    public string?  ResolutionStatus  { get; set; }

    public string?  Severity          { get; set; }
    public string?  IncidentCode      { get; set; }
    public string?  Category          { get; set; }
    public int?     SoDocNum          { get; set; }
    public string?  ItemCode          { get; set; }
    public long?    FragmentId        { get; set; }
    public long?    OrchestrationId   { get; set; }
    public DateTime? DateFrom         { get; set; }
    public DateTime? DateTo           { get; set; }

    /// <summary>null = all, true = only with resolution, false = only without</summary>
    public bool?    HasResolution     { get; set; }

    /// <summary>Filter by aging bucket: &lt;1h | 1-4h | 4-24h | 1-3d | &gt;3d</summary>
    public string?  AgingBucket       { get; set; }

    public int      Page              { get; set; } = 1;
    public int      PageSize          { get; set; } = 50;

    /// <summary>Sort field: detectedAt | severity | soDocNum | agingBucket | resolutionStatus</summary>
    public string   Sort              { get; set; } = "detectedAt";

    /// <summary>Sort direction: asc | desc</summary>
    public string   SortDir           { get; set; } = "desc";
}
