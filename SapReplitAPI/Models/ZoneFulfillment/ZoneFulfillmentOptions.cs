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
}
