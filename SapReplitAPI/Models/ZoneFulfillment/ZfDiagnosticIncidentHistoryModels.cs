namespace SapReplitAPI.Models.ZoneFulfillment;

/// <summary>
/// Durable technical lifecycle status. Independent of ZfIncidentResolutions
/// (human decision status) — see ZfIncidentStatusValue for that dimension.
/// </summary>
public static class ZfLifecycleStatus
{
    public const string Active    = "ACTIVE";
    public const string Recovered = "RECOVERED";
}

/// <summary>Recorded when a lifecycle row transitions ACTIVE → RECOVERED.</summary>
public static class ZfRecoveryReason
{
    /// <summary>The authoritative observation query no longer detected this incident.</summary>
    public const string NotDetectedInLiveScan = "NOT_DETECTED_IN_LIVE_SCAN";
}

/// <summary>Identifies which subsystem produced an observation. Currently always the job.</summary>
public static class ZfDetectionSource
{
    public const string ObservationJob = "ZfIncidentObservationJob";
}

/// <summary>
/// One durable occurrence of a technical incident, keyed by (IncidentKey, OccurrenceNumber).
///
/// Lifecycle: a new occurrence is created ACTIVE on first detection. While the incident
/// keeps being detected on subsequent observations, the SAME row is updated
/// (LastObservedAtUtc, EvidenceJson). When an observation no longer detects it, the row
/// is marked RECOVERED (ClearedAtUtc set) and becomes immutable. If the same IncidentKey
/// is detected again later, a NEW row is inserted with OccurrenceNumber+1 — this preserves
/// full recurrence history instead of losing it by reusing/reopening the old row.
///
/// EvidenceJson semantics: latest-observation snapshot. Overwritten on every ACTIVE
/// observation; frozen at whatever it held once the row is marked RECOVERED (i.e. it
/// reflects the last observation immediately before recovery, not first-detection evidence).
/// The row itself is never deleted, so no forensic data is lost — only the *first*-detection
/// evidence specifically is not separately retained if it differed from the latest.
///
/// This table is completely independent of dbo.ZfIncidentResolutions (human decision audit).
/// Technical recovery never creates/implies a human resolution, and a human resolution never
/// marks a technical incident recovered — the two are joined only by IncidentKey, read-only,
/// at the dashboard layer.
/// </summary>
public sealed class ZfDiagnosticIncidentHistoryRecord
{
    public long     Id                 { get; set; }
    public string   IncidentKey        { get; set; } = string.Empty;
    public int      OccurrenceNumber   { get; set; }
    public int      SoDocNum           { get; set; }
    public int?     SoDocEntry         { get; set; }
    public long?    OrchestrationId    { get; set; }
    public long?    FragmentId         { get; set; }
    public string   IncidentCode       { get; set; } = string.Empty;
    public string   Severity           { get; set; } = string.Empty;
    public string?  ItemCode           { get; set; }
    public int?     ExpectedLineNum    { get; set; }
    public string?  ExpectedWhsCode    { get; set; }
    public decimal? ExpectedQty        { get; set; }
    public string   LifecycleStatus    { get; set; } = ZfLifecycleStatus.Active;
    public DateTime FirstDetectedAtUtc { get; set; }
    public DateTime LastObservedAtUtc  { get; set; }
    public DateTime? ClearedAtUtc      { get; set; }
    public string?  RecoveryReason     { get; set; }
    public string   DetectionSource    { get; set; } = ZfDetectionSource.ObservationJob;
    public string?  EvidenceJson       { get; set; }
    public DateTime CreatedAtUtc       { get; set; }
    public DateTime UpdatedAtUtc       { get; set; }
}

/// <summary>One row detected by the authoritative live scan, before it's diffed against history.</summary>
public sealed class ZfLiveIncidentObservation
{
    public string  IncidentKey      { get; set; } = string.Empty;
    public int     SoDocNum         { get; set; }
    public int?    SoDocEntry       { get; set; }
    public long    OrchestrationId  { get; set; }
    public long    FragmentId       { get; set; }
    public int     SoLineNum        { get; set; }
    public string  ItemCode         { get; set; } = string.Empty;
    public string  WhsCode          { get; set; } = string.Empty;
    public decimal SoLineQty        { get; set; }
    public string  OrchestrationState { get; set; } = string.Empty;
    public string  IncidentCode     { get; set; } = ZfConsistencyStatus.FragmentRdr1Missing;
    public string  Severity         { get; set; } = ZfDiagnosticSeverity.High;
}

/// <summary>Result of one ObserveAsync() cycle — returned for logging, never exposed via API.</summary>
public sealed class ZfIncidentObservationResult
{
    public bool   Success        { get; set; }
    public string? Error         { get; set; }
    public int    Detected       { get; set; }
    public int    Created        { get; set; }
    public int    Updated        { get; set; }
    public int    Recovered      { get; set; }
    public double ElapsedMs      { get; set; }
}
