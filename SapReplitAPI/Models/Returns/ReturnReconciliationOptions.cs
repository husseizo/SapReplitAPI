namespace SapReplitAPI.Models.Returns;

/// <summary>
/// Feature flag + runtime config for the Return Request → Neon reconciliation safety net.
/// Bind from appsettings.json "ReturnReconciliation" section.
///
/// This job is a SAFETY NET only. The event-driven path (ReturnRequestEventHandler ->
/// ReturnRequestCacheService / NeonReturnRequestWriteService) remains the primary
/// synchronization mechanism and is not modified by this feature.
///
/// Default: Enabled=false and FullReconciliationEnabled=false -> both jobs are a no-op.
/// Set true only after controlled deployment verification, per PRODUCTION_RULES.md Rule 7.
/// </summary>
public sealed class ReturnReconciliationOptions
{
    public const string Section = "ReturnReconciliation";

    /// <summary>Master switch for the incremental (lookback-window) reconciliation job.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Master switch for the full (whole-ORRR/ORIN-table) reconciliation job.</summary>
    public bool FullReconciliationEnabled { get; set; } = false;

    /// <summary>
    /// Incremental job cron expression (Quartz format). Read directly from configuration at
    /// startup, not hard-coded, so the schedule can change without a code deployment.
    /// Default: hourly at minute 0.
    /// </summary>
    public string IncrementalCron { get; set; } = "0 0 * * * ?";

    /// <summary>
    /// Full reconciliation job cron expression. Intended to run rarely (e.g. nightly) because
    /// it reuses ReturnsMirrorBackfillService, which re-enumerates every ORRR/ORIN document and
    /// briefly holds a SHARE ROW EXCLUSIVE table lock on Neon. Default: 03:30 daily.
    /// </summary>
    public string FullCron { get; set; } = "0 30 3 * * ?";

    /// <summary>
    /// How far back the incremental job looks for changed ORRR documents, in minutes.
    /// Must be comfortably larger than the gap between two incremental runs (derived from
    /// IncrementalCron) so a single missed/slow run cannot create a coverage hole — the next
    /// run's overlapping window re-examines anything the previous run might have missed.
    /// Default: 180 (3x the default 60-minute run interval).
    /// </summary>
    public int LookbackMinutes { get; set; } = 180;

    /// <summary>
    /// Maximum number of candidate ORRR DocEntries refreshed in a single incremental run.
    /// Caps per-run SAP DI API load (one round-trip per candidate). Any candidates beyond this
    /// limit are simply left for the next run — safe because the lookback window overlaps.
    /// Default: 50.
    /// </summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>
    /// Soft wall-clock budget for one incremental run, in seconds. The job stops starting new
    /// candidate refreshes once this elapses (a refresh already in flight is allowed to finish)
    /// and logs how many candidates were deferred to the next run. Default: 240 (4 minutes).
    /// </summary>
    public int MaxExecutionSeconds { get; set; } = 240;

    // ── Historical rotation (Phase 4 TASK 2) ──────────────────────────────────────────────
    // A third tier, distinct from both the incremental (lookback-window) job and the full
    // (whole-table-lock) job: it eventually revisits EVERY ORRR DocEntry ever created — not just
    // recently-changed ones — through the same lightweight per-document refresh path, bounded by
    // BatchSize per run with its own persisted cursor, so it never needs a long-running table
    // lock and never repeats only the newest documents. This is what actually closes the
    // delayed-cancellation gap the incremental job's timestamp-based discovery cannot prove.

    /// <summary>Master switch for the historical rotation job.</summary>
    public bool HistoricalRotationEnabled { get; set; } = false;

    /// <summary>Historical rotation job cron expression. Default: every 15 minutes.</summary>
    public string HistoricalRotationCron { get; set; } = "0 0/15 * * * ?";

    /// <summary>
    /// Number of ORRR DocEntries refreshed per historical rotation run, oldest-not-yet-visited
    /// first (via its own rotation cursor, independent of the incremental job's cursor). Default: 10.
    /// </summary>
    public int HistoricalBatchSize { get; set; } = 10;

    /// <summary>Soft wall-clock budget for one historical rotation run, in seconds. Default: 120.</summary>
    public int HistoricalMaxExecutionSeconds { get; set; } = 120;
}
