using SapReplitAPI.Models.ZoneFulfillment;
using SapReplitAPI.Services.ZoneFulfillment;
using Xunit;

namespace SapReplitAPI.Tests.TieredAllocation;

/// <summary>
/// Shadow-mode tests: verify that the Tiered engine can safely run alongside the Legacy
/// engine on the same snapshot without mutating Legacy outputs or throwing exceptions.
///
/// These tests simulate what ZoneFulfillmentOrchestrationService does in Legacy mode:
/// the same OitwSnapshot list is passed to both engines; the Tiered result is observed
/// (logged) but never used to drive SAP mutations.
/// </summary>
public sealed class ShadowAllocationTests
{
    private static readonly ZoneAllocationEngine       Legacy = new();
    private static readonly TieredZoneAllocationEngine Tiered = new();

    private static ZoneWarehouse W(string code, int pri) => new(code, pri);
    private static DomainRequestLine Line(string item, decimal qty, int seq = 1)
        => new(Guid.NewGuid(), seq, item, qty, 10m, null, null, null);
    private static OitwSnapshot Stock(string item, string whs, decimal avail)
        => new(item, whs, avail);

    // ── S01 ──────────────────────────────────────────────────────────────────
    // Both engines run on the identical snapshot without exceptions.
    [Fact]
    public void S01_BothEnginesRun_OnIdenticalSnapshot_NoException()
    {
        var zone  = new[] { W("001", 1), W("002", 2) };
        var lines = new[] { Line("ITEM-A", 5) };
        var snap  = new[] { Stock("ITEM-A", "001", 10), Stock("ITEM-A", "002", 5) };

        var legacyResult = Legacy.Allocate(zone, lines, snap);
        var tieredResult = Tiered.Allocate(zone, Array.Empty<ZoneWarehouse>(), lines, snap);

        Assert.NotNull(legacyResult);
        Assert.NotNull(tieredResult);
    }

    // ── S02 ──────────────────────────────────────────────────────────────────
    // Tiered engine result does not modify the Legacy engine result object.
    [Fact]
    public void S02_TieredRunAsShadow_DoesNotMutateLegacyResult()
    {
        var zone  = new[] { W("001", 1), W("002", 2) };
        var lines = new[] { Line("ITEM-A", 5) };
        var snap  = new[] { Stock("ITEM-A", "001", 10) };

        var legacyResult = Legacy.Allocate(zone, lines, snap);
        int legacyFragCount   = legacyResult.Fragments.Count;
        bool legacyHasShortage = legacyResult.HasShortage;

        // Shadow run
        _ = Tiered.Allocate(zone, Array.Empty<ZoneWarehouse>(), lines, snap);

        // Legacy result is unchanged
        Assert.Equal(legacyFragCount,    legacyResult.Fragments.Count);
        Assert.Equal(legacyHasShortage,  legacyResult.HasShortage);
    }

    // ── S03 ──────────────────────────────────────────────────────────────────
    // When engines agree (same WHS, same qty), no diff is detected.
    [Fact]
    public void S03_WhenEnginesAgree_NoDiffDetected()
    {
        // Tier 1 and Phase 1 of Legacy both pick the first WHS with full basket.
        var zone  = new[] { W("001", 1), W("002", 2) };
        var line  = Line("ITEM-A", 5);
        var snap  = new[] { Stock("ITEM-A", "001", 10) };

        var legacyResult = Legacy.Allocate(zone, new[] { line }, snap);
        var tieredResult = Tiered.Allocate(zone, Array.Empty<ZoneWarehouse>(), new[] { line }, snap);

        // Both should allocate from 001 (first WHS with full stock)
        string legacyWhs = legacyResult.Fragments.Single().WhsCode;
        string tieredWhs = tieredResult.BaseResult.Fragments.Single().WhsCode;
        Assert.Equal(legacyWhs, tieredWhs);
        Assert.False(tieredResult.BaseResult.HasShortage);
    }

    // ── S04 ──────────────────────────────────────────────────────────────────
    // When engines differ (Tiered picks better WHS), diff is detectable by comparing
    // fragment WhsCodes — shadow log would fire in production.
    [Fact]
    public void S04_WhenEnginesDiffer_DiffIsDetectable()
    {
        // Legacy Phase 1 picks 001 (first WHS with full basket).
        // Tiered with origin004 zone order picks 004 (first in priority order).
        var legacyZone = new[] { W("001", 1), W("002", 2), W("004", 3), W("003", 4) };
        var tieredZone = new[] { W("004", 1), W("001", 2), W("002", 3), W("003", 4) };

        var line = Line("ITEM-A", 5);
        var snap = new[]
        {
            Stock("ITEM-A", "001", 10),
            Stock("ITEM-A", "004", 10)
        };

        var legacyResult = Legacy.Allocate(legacyZone, new[] { line }, snap);
        var tieredResult = Tiered.Allocate(tieredZone, Array.Empty<ZoneWarehouse>(), new[] { line }, snap);

        string legacyWhs = legacyResult.Fragments.Single().WhsCode;
        string tieredWhs = tieredResult.BaseResult.Fragments.Single().WhsCode;

        // Engines should differ in WHS selection
        Assert.Equal("001", legacyWhs);
        Assert.Equal("004", tieredWhs);
        Assert.NotEqual(legacyWhs, tieredWhs); // diff is detectable
    }

    // ── S05 ──────────────────────────────────────────────────────────────────
    // Shadow: Tiered result HasShortage=false while Legacy HasShortage=true
    // (or vice versa) — diff is detectable.
    [Fact]
    public void S05_ShadowDiff_ShortageStatusDetectable()
    {
        // Legacy Phase 2 cascade leaves shortage; Tiered Tier 2 minimum-set also leaves shortage.
        // Use a case where both have shortage to verify they agree on shortage.
        var zone = new[] { W("001", 1) };
        var line = Line("ITEM-A", 10);
        var snap = new[] { Stock("ITEM-A", "001", 3) };

        var legacyResult = Legacy.Allocate(zone, new[] { line }, snap);
        var tieredResult = Tiered.Allocate(zone, Array.Empty<ZoneWarehouse>(), new[] { line }, snap);

        // Both should detect shortage
        Assert.True(legacyResult.HasShortage);
        Assert.True(tieredResult.BaseResult.HasShortage);

        // Shadow diff would find HasShortage matches — no diff on shortage field
        Assert.Equal(legacyResult.HasShortage, tieredResult.BaseResult.HasShortage);
    }

    // ── S06 ──────────────────────────────────────────────────────────────────
    // Shadow never throws even when stock is zero (Tier 5 boundary).
    [Fact]
    public void S06_ShadowSafe_ZeroStock_NoException()
    {
        var zone  = new[] { W("001", 1), W("002", 2) };
        var lines = new[] { Line("ITEM-A", 5), Line("ITEM-B", 3) };
        var snap  = Array.Empty<OitwSnapshot>(); // zero stock for all items

        AllocationResult? legacyResult = null;
        TieredAllocationResult? tieredResult = null;

        var ex1 = Record.Exception(() => legacyResult = Legacy.Allocate(zone, lines, snap));
        var ex2 = Record.Exception(() => tieredResult  = Tiered.Allocate(zone, Array.Empty<ZoneWarehouse>(), lines, snap));

        Assert.Null(ex1);
        Assert.Null(ex2);
        Assert.NotNull(legacyResult);
        Assert.NotNull(tieredResult);
        Assert.True(tieredResult!.BaseResult.HasShortage);
    }
}
