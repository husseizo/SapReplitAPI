namespace SapReplitAPI.Models.ZoneFulfillment;

/// <summary>
/// Strongly-typed options for Zone Fulfillment configuration.
/// Bound from appsettings section "ZoneFulfillment".
/// </summary>
public sealed class ZoneFulfillmentOptions
{
    public const string Section = "ZoneFulfillment";

    /// <summary>
    /// Zone name applied when the client supplies a blank DeliveryLocation.
    /// Must be a non-empty string matching an active row in dbo.ZoneWarehousePriority.
    /// Example: "Cluster-side"
    /// </summary>
    public string DefaultZone { get; init; } = "";

    /// <summary>
    /// Allocation mode: "Legacy" or "Tiered".
    /// Invalid or missing values default to "Legacy" with a startup warning.
    /// Production default: "Legacy" — do not change without Gate 2 approval.
    /// </summary>
    public string AllocationMode { get; init; } = "Legacy";

    /// <summary>Effective origin used for Cluster-side when originWhsCode is absent or unknown.</summary>
    public string TieredClusterDefault   { get; init; } = "001";

    /// <summary>Effective origin used for Mikocheni-side (all origins normalize to this before lookup).</summary>
    public string TieredMikocheniDefault { get; init; } = "003";

    /// <summary>
    /// D1: Canonical zone-membership map used to split ordered priority rows into
    /// HomeZone and FallbackZone. Keys must match zone names in dbo.ZoneWarehousePriority.
    /// Default: Cluster-side = [001,002,004]; Mikocheni-side = [003].
    /// Override in appsettings.json under ZoneFulfillment:ZoneMembers.
    /// </summary>
    public Dictionary<string, List<string>> ZoneMembers { get; init; } = new()
    {
        ["Cluster-side"]   = ["001", "002", "004"],
        ["Mikocheni-side"] = ["003"]
    };
}
