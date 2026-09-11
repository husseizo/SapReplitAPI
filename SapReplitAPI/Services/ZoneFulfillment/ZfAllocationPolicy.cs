using Microsoft.Extensions.Options;
using SapReplitAPI.Models.ZoneFulfillment;

namespace SapReplitAPI.Services.ZoneFulfillment;

/// <summary>
/// Coordinator-free shared allocation policy.
/// Implements mode-switching (Legacy ↔ Tiered) used by both the /orders orchestration
/// and the /plan dry-run endpoint. Caller owns locking when needed.
/// snapshots must be pre-fetched from OITW for all zone warehouses.
///
/// D1: In Tiered mode, the resolver now produces HomeZone and FallbackZone separately.
/// The engine receives both — Tier 1/2 search HOME only; Tier 3+ may use FALLBACK.
/// </summary>
public sealed class ZfAllocationPolicy
{
    private readonly ZoneAllocationEngine            _legacyAllocator;
    private readonly TieredZoneAllocationEngine      _tieredEngine;
    private readonly TieredWarehousePriorityResolver _tieredResolver;
    private readonly ZoneFulfillmentOptions          _opts;

    public ZfAllocationPolicy(
        ZoneAllocationEngine             legacyAllocator,
        TieredZoneAllocationEngine       tieredEngine,
        TieredWarehousePriorityResolver  tieredResolver,
        IOptions<ZoneFulfillmentOptions> opts)
    {
        _legacyAllocator = legacyAllocator;
        _tieredEngine    = tieredEngine;
        _tieredResolver  = tieredResolver;
        _opts            = opts.Value;
    }

    public async Task<ZfAllocationPolicyResult> AllocateAsync(
        string?                          originWhsCode,
        string                           deliveryLocation,
        IReadOnlyList<ZoneWarehouse>     zone,
        IReadOnlyList<DomainRequestLine> domainLines,
        IReadOnlyList<OitwSnapshot>      snapshots,
        CancellationToken                ct)
    {
        bool tieredMode = string.Equals(
            _opts.AllocationMode, AllocationMode.Tiered,
            StringComparison.OrdinalIgnoreCase);

        if (tieredMode)
        {
            var ctx    = await _tieredResolver.ResolveAsync(originWhsCode, deliveryLocation, ct);
            var result = _tieredEngine.Allocate(ctx.HomeZone, ctx.FallbackZone, domainLines, snapshots);
            return new ZfAllocationPolicyResult
            {
                Allocation       = result.BaseResult,
                ReceivedOrigin   = ctx.ReceivedOrigin,
                EffectiveOrigin  = ctx.EffectiveOrigin,
                HomeZone         = ctx.HomeZone,
                FallbackZone     = ctx.FallbackZone,
                Mode             = AllocationMode.Tiered,
                AllocationTier   = result.AllocationTier,
                AllocationReason = result.AllocationReason,
                FragmentTiers    = result.FragmentTiers
            };
        }
        else
        {
            var allocation = _legacyAllocator.Allocate(zone, domainLines, snapshots);
            return new ZfAllocationPolicyResult
            {
                Allocation       = allocation,
                ReceivedOrigin   = originWhsCode ?? "",
                EffectiveOrigin  = originWhsCode ?? "",
                HomeZone         = [],
                FallbackZone     = [],
                Mode             = AllocationMode.Legacy,
                AllocationTier   = 0,
                AllocationReason = "Legacy",
                FragmentTiers    = []
            };
        }
    }
}

public sealed class ZfAllocationPolicyResult
{
    public required AllocationResult Allocation       { get; init; }
    public required string           ReceivedOrigin   { get; init; }
    public required string           EffectiveOrigin  { get; init; }
    public required string           Mode             { get; init; }
    public          int              AllocationTier   { get; init; }
    public          string           AllocationReason { get; init; } = "";
    public IReadOnlyList<(Guid RequestLineId, string WhsCode, int SourceTier)> FragmentTiers { get; init; } = [];

    // D1: exposed for /plan response and production verification
    public IReadOnlyList<ZoneWarehouse> HomeZone     { get; init; } = [];
    public IReadOnlyList<ZoneWarehouse> FallbackZone { get; init; } = [];
}
