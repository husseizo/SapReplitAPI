using Microsoft.Extensions.Logging;
using SapReplitAPI.Models.ZoneFulfillment;

namespace SapReplitAPI.Services.ZoneFulfillment;

/// <summary>
/// Reads fresh OITW stock from SAP for zone allocation.
/// Available = max(0, OnHand − IsCommited).
/// Delegates COM access to SapService.GetOitwAvailable().
/// Registered Scoped.
/// </summary>
public sealed class SapOitwAdapter
{
    private readonly SapService _sap;
    private readonly ILogger<SapOitwAdapter> _log;

    public SapOitwAdapter(SapService sap, ILogger<SapOitwAdapter> log)
    {
        _sap = sap;
        _log = log;
    }

    /// <summary>
    /// For each (ItemCode × WhsCode) pair needed by the request lines + zone,
    /// returns OitwSnapshot with Available = max(0, OnHand − IsCommited).
    /// Missing rows are returned as Available=0 (not an error — item not stocked in that WHS).
    /// </summary>
    public List<OitwSnapshot> GetSnapshots(
        IReadOnlyList<string> itemCodes,
        IReadOnlyList<ZoneWarehouse> zone)
    {
        var pairs = itemCodes
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .SelectMany(item => zone.Select(whs => (item, whs.WhsCode)))
            .ToList();

        _log.LogDebug("[SapOitwAdapter] Querying {PairCount} OITW pairs for {ItemCount} items × {WhsCount} WHS.",
            pairs.Count, itemCodes.Distinct(StringComparer.OrdinalIgnoreCase).Count(), zone.Count);

        var raw = _sap.GetOitwAvailable(pairs);

        return raw.Select(r => new OitwSnapshot(r.ItemCode, r.WhsCode, r.Available)).ToList();
    }
}
