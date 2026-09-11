using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SapReplitAPI.Models.ZoneFulfillment;
using SapReplitAPI.Services.ZoneFulfillment;
using Xunit;

namespace SapReplitAPI.Tests.TieredAllocation;

/// <summary>
/// D1 resolver tests: verify that TieredWarehousePriorityResolver correctly splits
/// priority rows into HomeZone and FallbackZone for every origin/zone combination.
/// All DB calls are replaced by FakeZfPriorityRepo — no SQL, no SAP.
/// </summary>
public sealed class ResolverD1Tests
{
    private static TieredWarehousePriorityResolver MakeResolver(
        FakeZfPriorityRepo repo,
        ZoneFulfillmentOptions? opts = null)
    {
        var o = opts ?? new ZoneFulfillmentOptions
        {
            DefaultZone            = "Cluster-side",
            AllocationMode         = "Tiered",
            TieredClusterDefault   = "001",
            TieredMikocheniDefault = "003",
            ZoneMembers            = new Dictionary<string, List<string>>
            {
                ["Cluster-side"]   = ["001", "002", "004"],
                ["Mikocheni-side"] = ["003"]
            }
        };
        return new TieredWarehousePriorityResolver(
            repo,
            Options.Create(o),
            NullLogger<TieredWarehousePriorityResolver>.Instance);
    }

    // ── R-D1-01 ──────────────────────────────────────────────────────────────
    // Cluster origin=004: HomeZone = rows in {001,002,004}; FallbackZone = {003}.
    [Fact]
    public async Task RD101_ClusterOrigin004_Home001_002_004_Fallback003()
    {
        var repo = new FakeZfPriorityRepo("Cluster-side", "004",
            new[] {
                new OriginWarehousePriorityRow("Cluster-side", "004", "004", 1),
                new OriginWarehousePriorityRow("Cluster-side", "004", "001", 2),
                new OriginWarehousePriorityRow("Cluster-side", "004", "002", 3),
                new OriginWarehousePriorityRow("Cluster-side", "004", "003", 4),
            });

        var ctx = await MakeResolver(repo).ResolveAsync("004", "Cluster-side");

        Assert.Equal(3, ctx.HomeZone.Count);
        Assert.Contains(ctx.HomeZone, w => w.WhsCode == "004");
        Assert.Contains(ctx.HomeZone, w => w.WhsCode == "001");
        Assert.Contains(ctx.HomeZone, w => w.WhsCode == "002");
        Assert.Single(ctx.FallbackZone);
        Assert.Equal("003", ctx.FallbackZone[0].WhsCode);
    }

    // ── R-D1-02 ──────────────────────────────────────────────────────────────
    // Cluster origin=001: HomeZone = {001,002,004}; FallbackZone = {003}.
    [Fact]
    public async Task RD102_ClusterOrigin001_Home001_002_004_Fallback003()
    {
        var repo = new FakeZfPriorityRepo("Cluster-side", "001",
            new[] {
                new OriginWarehousePriorityRow("Cluster-side", "001", "001", 1),
                new OriginWarehousePriorityRow("Cluster-side", "001", "002", 2),
                new OriginWarehousePriorityRow("Cluster-side", "001", "004", 3),
                new OriginWarehousePriorityRow("Cluster-side", "001", "003", 4),
            });

        var ctx = await MakeResolver(repo).ResolveAsync("001", "Cluster-side");

        Assert.Equal(3, ctx.HomeZone.Count);
        Assert.DoesNotContain(ctx.HomeZone,     w => w.WhsCode == "003");
        Assert.Single(ctx.FallbackZone);
        Assert.Equal("003", ctx.FallbackZone[0].WhsCode);
    }

    // ── R-D1-03 ──────────────────────────────────────────────────────────────
    // Cluster A1 special case: origin=003 → ALL four WHS in HomeZone, FallbackZone empty.
    [Fact]
    public async Task RD103_ClusterOrigin003_A1_AllFourInHome_EmptyFallback()
    {
        var repo = new FakeZfPriorityRepo("Cluster-side", "003",
            new[] {
                new OriginWarehousePriorityRow("Cluster-side", "003", "003", 1),
                new OriginWarehousePriorityRow("Cluster-side", "003", "004", 2),
                new OriginWarehousePriorityRow("Cluster-side", "003", "001", 3),
                new OriginWarehousePriorityRow("Cluster-side", "003", "002", 4),
            });

        var ctx = await MakeResolver(repo).ResolveAsync("003", "Cluster-side");

        Assert.Equal(4, ctx.HomeZone.Count);
        Assert.Empty(ctx.FallbackZone);
    }

    // ── R-D1-04 ──────────────────────────────────────────────────────────────
    // Mikocheni zone (any origin): HomeZone = {003} only; FallbackZone = rest.
    [Fact]
    public async Task RD104_MikocheniZone_Home003Only_FallbackIsRest()
    {
        // Mikocheni always normalizes lookupOrigin to "003"
        var repo = new FakeZfPriorityRepo("Mikocheni-side", "003",
            new[] {
                new OriginWarehousePriorityRow("Mikocheni-side", "003", "003", 1),
                new OriginWarehousePriorityRow("Mikocheni-side", "003", "004", 2),
                new OriginWarehousePriorityRow("Mikocheni-side", "003", "001", 3),
            });

        // Any origin passed for Mikocheni → normalizes to 003
        var ctx = await MakeResolver(repo).ResolveAsync("001", "Mikocheni-side");

        Assert.Single(ctx.HomeZone);
        Assert.Equal("003", ctx.HomeZone[0].WhsCode);
        Assert.Equal(2, ctx.FallbackZone.Count);
        Assert.DoesNotContain(ctx.FallbackZone, w => w.WhsCode == "003");
    }

    // ── R-D1-05 ──────────────────────────────────────────────────────────────
    // Legacy fallback (no OriginWarehousePriority rows): all zone warehouses go to HomeZone.
    [Fact]
    public async Task RD105_LegacyFallback_NoOriginRows_AllAsHome()
    {
        var repo = new FakeZfPriorityRepo("Cluster-side", "004",
            originRows: Array.Empty<OriginWarehousePriorityRow>(),
            legacyRows: new[] { new ZoneWarehouse("004", 1), new ZoneWarehouse("001", 2) });

        var ctx = await MakeResolver(repo).ResolveAsync("004", "Cluster-side");

        Assert.Equal(2, ctx.HomeZone.Count);
        Assert.Empty(ctx.FallbackZone);
    }

    // ── R-D1-06 ──────────────────────────────────────────────────────────────
    // TieredZone = HomeZone + FallbackZone concatenated (deduplicated).
    [Fact]
    public async Task RD106_TieredZone_IsHomePlusFallback_Deduped()
    {
        var repo = new FakeZfPriorityRepo("Cluster-side", "004",
            new[] {
                new OriginWarehousePriorityRow("Cluster-side", "004", "004", 1),
                new OriginWarehousePriorityRow("Cluster-side", "004", "001", 2),
                new OriginWarehousePriorityRow("Cluster-side", "004", "002", 3),
                new OriginWarehousePriorityRow("Cluster-side", "004", "003", 4),
            });

        var ctx = await MakeResolver(repo).ResolveAsync("004", "Cluster-side");

        // TieredZone must include all four WHS exactly once
        Assert.Equal(4, ctx.TieredZone.Count);
        Assert.Equal(ctx.TieredZone.Count,
            ctx.TieredZone.Select(w => w.WhsCode).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    // ── R-D1-07 ──────────────────────────────────────────────────────────────
    // EffectiveOrigin: blank/null origin → zone default (Cluster=001, Mikocheni=003).
    [Fact]
    public async Task RD107_BlankOrigin_ResolvesToZoneDefault()
    {
        var repo = new FakeZfPriorityRepo("Cluster-side", "001",
            new[] {
                new OriginWarehousePriorityRow("Cluster-side", "001", "001", 1),
                new OriginWarehousePriorityRow("Cluster-side", "001", "002", 2),
                new OriginWarehousePriorityRow("Cluster-side", "001", "004", 3),
                new OriginWarehousePriorityRow("Cluster-side", "001", "003", 4),
            });

        var ctx = await MakeResolver(repo).ResolveAsync(null, "Cluster-side");

        Assert.Equal("001", ctx.EffectiveOrigin);
        Assert.Equal("", ctx.ReceivedOrigin);
    }

    // ── R-D1-08 ──────────────────────────────────────────────────────────────
    // Unknown origin → log warning + resolve to zone default; HomeZone/FallbackZone are
    // based on the default origin, not the unknown one.
    [Fact]
    public async Task RD108_UnknownOrigin_ResolvesToZoneDefault_HomeFromDefault()
    {
        var repo = new FakeZfPriorityRepo("Cluster-side", "001",  // default lookup
            new[] {
                new OriginWarehousePriorityRow("Cluster-side", "001", "001", 1),
                new OriginWarehousePriorityRow("Cluster-side", "001", "002", 2),
                new OriginWarehousePriorityRow("Cluster-side", "001", "004", 3),
                new OriginWarehousePriorityRow("Cluster-side", "001", "003", 4),
            });

        var ctx = await MakeResolver(repo).ResolveAsync("999", "Cluster-side");

        Assert.Equal("001", ctx.EffectiveOrigin);
        Assert.Equal("999", ctx.ReceivedOrigin);
        // HomeZone for default 001 = {001, 002, 004}
        Assert.Equal(3, ctx.HomeZone.Count);
        Assert.Single(ctx.FallbackZone);
    }

    // ── R-D1-09 ──────────────────────────────────────────────────────────────
    // HomeZone priority order is preserved from OriginWarehousePriority rows.
    [Fact]
    public async Task RD109_HomeZone_PriorityOrderPreserved()
    {
        var repo = new FakeZfPriorityRepo("Cluster-side", "004",
            new[] {
                new OriginWarehousePriorityRow("Cluster-side", "004", "004", 1),
                new OriginWarehousePriorityRow("Cluster-side", "004", "001", 2),
                new OriginWarehousePriorityRow("Cluster-side", "004", "002", 3),
                new OriginWarehousePriorityRow("Cluster-side", "004", "003", 4),
            });

        var ctx = await MakeResolver(repo).ResolveAsync("004", "Cluster-side");

        // HomeZone = 004 (pri=1) → 001 (pri=2) → 002 (pri=3)
        Assert.Equal("004", ctx.HomeZone[0].WhsCode);
        Assert.Equal("001", ctx.HomeZone[1].WhsCode);
        Assert.Equal("002", ctx.HomeZone[2].WhsCode);
    }

    // ── R-D1-10 ──────────────────────────────────────────────────────────────
    // Cluster origin=002: HomeZone = {001,002,004} ordered by their row priorities.
    [Fact]
    public async Task RD110_ClusterOrigin002_HomeContains001_002_004()
    {
        var repo = new FakeZfPriorityRepo("Cluster-side", "002",
            new[] {
                new OriginWarehousePriorityRow("Cluster-side", "002", "002", 1),
                new OriginWarehousePriorityRow("Cluster-side", "002", "001", 2),
                new OriginWarehousePriorityRow("Cluster-side", "002", "004", 3),
                new OriginWarehousePriorityRow("Cluster-side", "002", "003", 4),
            });

        var ctx = await MakeResolver(repo).ResolveAsync("002", "Cluster-side");

        Assert.Equal(3, ctx.HomeZone.Count);
        Assert.Contains(ctx.HomeZone, w => w.WhsCode == "002");
        Assert.Contains(ctx.HomeZone, w => w.WhsCode == "001");
        Assert.Contains(ctx.HomeZone, w => w.WhsCode == "004");
        Assert.Single(ctx.FallbackZone);
        Assert.Equal("003", ctx.FallbackZone[0].WhsCode);
    }
}

/// <summary>
/// In-memory stub for IZfPriorityRepo used in resolver tests.
/// Returns a fixed set of OriginWarehousePriority rows for a specific zone+origin lookup.
/// </summary>
internal sealed class FakeZfPriorityRepo : IZfPriorityRepo
{
    private readonly string _zone;
    private readonly string _origin;
    private readonly IReadOnlyList<OriginWarehousePriorityRow> _originRows;
    private readonly IReadOnlyList<ZoneWarehouse>              _legacyRows;

    public FakeZfPriorityRepo(
        string zone,
        string origin,
        IEnumerable<OriginWarehousePriorityRow> originRows,
        IEnumerable<ZoneWarehouse>? legacyRows = null)
    {
        _zone       = zone;
        _origin     = origin;
        _originRows = originRows.ToList();
        _legacyRows = legacyRows?.ToList() ?? [];
    }

    public Task<List<OriginWarehousePriorityRow>> GetOriginWarehousePriorityAsync(
        string zoneName, string originWhsCode, CancellationToken ct = default)
    {
        // Return rows only when zone and origin match; otherwise empty (triggers legacy fallback).
        bool match = string.Equals(zoneName,      _zone,   StringComparison.OrdinalIgnoreCase)
                  && string.Equals(originWhsCode, _origin, StringComparison.OrdinalIgnoreCase);
        return Task.FromResult(match ? _originRows.ToList() : new List<OriginWarehousePriorityRow>());
    }

    public Task<List<ZoneWarehouse>> GetZoneWarehousesAsync(
        string zoneName, CancellationToken ct = default)
        => Task.FromResult(_legacyRows.ToList());
}
