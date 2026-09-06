namespace SapReplitAPI.Models.Offline;

/// <summary>
/// Feature flag + runtime config for Offline Fulfillment V2.
/// Bind from appsettings.json "OfflineFulfillmentV2" section.
///
/// Default: Enabled=false → V2 endpoints return 503, V2 recovery job is a no-op.
/// Legacy V1 (PendingOrderSyncJob) operates regardless of this flag.
/// Normal Online ZF operates regardless of this flag.
/// </summary>
public sealed class OfflineFulfillmentOptions
{
    public const string Section = "OfflineFulfillmentV2";

    /// <summary>Master switch. Set true only after controlled deployment verification.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Max recovery workers per job cycle. Default: 5.</summary>
    public int RecoveryBatchSize { get; set; } = 5;

    /// <summary>Stale claim lease duration in seconds. Default: 120 (2 min).</summary>
    public int RecoveryClaimLeaseSeconds { get; set; } = 120;
}
