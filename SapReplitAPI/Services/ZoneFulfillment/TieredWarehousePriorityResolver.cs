using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SapReplitAPI.Models.ZoneFulfillment;

namespace SapReplitAPI.Services.ZoneFulfillment;

/// <summary>
/// Resolves the effective origin warehouse and builds origin-aware HomeZone / FallbackZone
/// lists for D1 Tiered allocation.
///
/// Rules:
///   - Mikocheni-side: all origins normalize to effective "003" before lookup.
///   - Cluster-side: origin 001/002/003/004 each have a distinct priority order.
///   - Missing/blank origin → zone canonical default (Cluster=001, Mikocheni=003) silently.
///   - Non-empty unknown origin → log [ZF-TIERED] UnknownOriginWhsCode, resolve to zone default.
///   - EffectiveOrigin always contains the actual WHS code — never the literal "DEFAULT".
///
/// D1 Home/Fallback split rules:
///   Cluster-side, origin 001/002/004:
///     HomeZone     = ordered priority rows whose WHS is in ZoneMembers["Cluster-side"]
///     FallbackZone = ordered priority rows whose WHS is NOT in home members (i.e. 003)
///   Cluster-side, origin 003 (A1 special case):
///     HomeZone     = ALL ordered priority rows (003 physically on cluster side for this order)
///     FallbackZone = [] empty
///   Mikocheni-side (all origins normalize to 003):
///     HomeZone     = only the 003 warehouse row
///     FallbackZone = remaining ordered rows (001, 002, 004)
///   Legacy fallback (no OriginWarehousePriority rows):
///     HomeZone     = ALL ZoneWarehouse rows returned from legacy lookup
///     FallbackZone = [] empty
///     Logged: [ZF-TIERED] LegacyPriorityFallbackAllHome
/// </summary>
public sealed class TieredWarehousePriorityResolver
{
    private const string ClusterZone   = "Cluster-side";
    private const string MikocheniZone = "Mikocheni-side";

    private static readonly string[] KnownOrigins = ["001", "002", "003", "004"];

    private readonly IZfPriorityRepo                                 _repo;
    private readonly ZoneFulfillmentOptions                         _opts;
    private readonly ILogger<TieredWarehousePriorityResolver>       _log;

    public TieredWarehousePriorityResolver(
        IZfPriorityRepo                          repo,
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

        IReadOnlyList<ZoneWarehouse> homeZone;
        IReadOnlyList<ZoneWarehouse> fallbackZone;

        if (rows.Count == 0)
        {
            _log.LogWarning(
                "[ZF-TIERED] LegacyPriorityFallbackAllHome: No OriginWarehousePriority rows for " +
                "Zone={Zone} OriginLookup={Origin} — falling back to legacy ZoneWarehousePriority. " +
                "All rows treated as HOME; FallbackZone=empty.",
                zoneName, lookupOrigin);

            var legacy = await _repo.GetZoneWarehousesAsync(zoneName, ct);
            homeZone     = legacy;
            fallbackZone = [];
        }
        else
        {
            var ordered = rows
                .OrderBy(r => r.Priority)
                .Select(r => new ZoneWarehouse(r.WhsCode, r.Priority))
                .ToList();

            (homeZone, fallbackZone) = SplitHomeAndFallback(ordered, zoneName, effectiveOrigin);
        }

        // TieredZone = home + fallback concatenated (backward-compat)
        var tieredZone = homeZone
            .Concat(fallbackZone)
            .DistinctBy(w => w.WhsCode, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new TieredAllocationContext
        {
            ReceivedOrigin  = receivedOrigin ?? "",
            EffectiveOrigin = effectiveOrigin,
            HomeZone        = homeZone,
            FallbackZone    = fallbackZone,
            TieredZone      = tieredZone
        };
    }

    // ── D1 Home/Fallback split ─────────────────────────────────────────────────

    private (IReadOnlyList<ZoneWarehouse> Home, IReadOnlyList<ZoneWarehouse> Fallback)
        SplitHomeAndFallback(
            List<ZoneWarehouse> ordered,
            string              zoneName,
            string              effectiveOrigin)
    {
        if (zoneName.Equals(MikocheniZone, StringComparison.OrdinalIgnoreCase))
        {
            // Mikocheni: HOME = 003 only; FALLBACK = rest
            var mikoHome     = ordered.Where(w => w.WhsCode.Equals("003", StringComparison.OrdinalIgnoreCase)).ToList();
            var mikoFallback = ordered.Where(w => !w.WhsCode.Equals("003", StringComparison.OrdinalIgnoreCase)).ToList();
            if (mikoHome.Count == 0)
                _log.LogWarning("[ZF-TIERED] Mikocheni resolver: no '003' row found in priority list — HomeZone empty.");
            return (mikoHome, mikoFallback);
        }

        // Cluster-side: load home members from config
        if (!_opts.ZoneMembers.TryGetValue(zoneName, out var homeMembers) || homeMembers.Count == 0)
        {
            _log.LogWarning(
                "[ZF-TIERED] No ZoneMembers config for Zone={Zone} — treating all rows as HOME.",
                zoneName);
            return (ordered, []);
        }

        var homeMemberSet = homeMembers
            .Select(w => w.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // A1 special case: Cluster + effectiveOrigin=003
        // Employee operating from 003 — 003 joins the home set for this order.
        if (effectiveOrigin.Equals("003", StringComparison.OrdinalIgnoreCase))
        {
            // All four warehouses are HOME; FALLBACK = empty
            _log.LogInformation(
                "[ZF-TIERED] A1 special case: Cluster origin=003 — all rows treated as HOME, FallbackZone=empty.");
            return (ordered, []);
        }

        // Normal Cluster case: split by home membership
        var home     = ordered.Where(w => homeMemberSet.Contains(w.WhsCode)).ToList();
        var fallback = ordered.Where(w => !homeMemberSet.Contains(w.WhsCode)).ToList();
        return (home, fallback);
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
