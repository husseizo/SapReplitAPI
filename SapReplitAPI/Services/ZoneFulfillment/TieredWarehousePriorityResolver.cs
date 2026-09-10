using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SapReplitAPI.Models.ZoneFulfillment;

namespace SapReplitAPI.Services.ZoneFulfillment;

/// <summary>
/// Resolves the effective origin warehouse and builds the origin-aware priority list
/// for Tiered allocation mode.
///
/// Rules:
///   - Mikocheni-side: all origins normalize to effective "003" before lookup.
///   - Cluster-side: origin 001/002/003/004 each have a distinct priority order.
///   - Missing/blank origin → zone canonical default (Cluster=001, Mikocheni=003) silently.
///   - Non-empty unknown origin → log [ZF-TIERED] UnknownOriginWhsCode, resolve to zone default.
///   - EffectiveOrigin always contains the actual WHS code — never the literal "DEFAULT".
/// </summary>
public sealed class TieredWarehousePriorityResolver
{
    private const string ClusterZone   = "Cluster-side";
    private const string MikocheniZone = "Mikocheni-side";

    private static readonly string[] KnownOrigins = ["001", "002", "003", "004"];

    private readonly ZoneFulfillmentRepository                      _repo;
    private readonly ZoneFulfillmentOptions                         _opts;
    private readonly ILogger<TieredWarehousePriorityResolver>       _log;

    public TieredWarehousePriorityResolver(
        ZoneFulfillmentRepository                repo,
        IOptions<ZoneFulfillmentOptions>         opts,
        ILogger<TieredWarehousePriorityResolver> log)
    {
        _repo = repo;
        _opts = opts.Value;
        _log  = log;
    }

    public async Task<TieredAllocationContext> ResolveAsync(
        string?           receivedOrigin,
        string            zoneName,
        CancellationToken ct = default)
    {
        string effectiveOrigin = DetermineEffectiveOrigin(receivedOrigin, zoneName);

        // Mikocheni always normalizes to effective "003" for the priority lookup
        string lookupOrigin = zoneName.Equals(MikocheniZone, StringComparison.OrdinalIgnoreCase)
            ? _opts.TieredMikocheniDefault
            : effectiveOrigin;

        var rows = await _repo.GetOriginWarehousePriorityAsync(zoneName, lookupOrigin, ct);

        IReadOnlyList<ZoneWarehouse> tieredZone;
        if (rows.Count == 0)
        {
            _log.LogWarning(
                "[ZF-TIERED] No OriginWarehousePriority rows for Zone={Zone} OriginLookup={Origin} — " +
                "falling back to legacy ZoneWarehousePriority order",
                zoneName, lookupOrigin);
            tieredZone = await _repo.GetZoneWarehousesAsync(zoneName, ct);
        }
        else
        {
            tieredZone = rows
                .OrderBy(r => r.Priority)
                .Select(r => new ZoneWarehouse(r.WhsCode, r.Priority))
                .ToList();
        }

        return new TieredAllocationContext
        {
            ReceivedOrigin  = receivedOrigin ?? "",
            EffectiveOrigin = effectiveOrigin,
            TieredZone      = tieredZone
        };
    }

    private string DetermineEffectiveOrigin(string? received, string zoneName)
    {
        string zoneDefault = zoneName.Equals(MikocheniZone, StringComparison.OrdinalIgnoreCase)
            ? _opts.TieredMikocheniDefault
            : _opts.TieredClusterDefault;

        if (string.IsNullOrWhiteSpace(received))
            return zoneDefault;

        string trimmed = received.Trim();
        if (!KnownOrigins.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
        {
            _log.LogWarning(
                "[ZF-TIERED] UnknownOriginWhsCode received={Received} zone={Zone} — " +
                "resolving to zone default={Default}",
                trimmed, zoneName, zoneDefault);
            return zoneDefault;
        }

        return trimmed;
    }
}
