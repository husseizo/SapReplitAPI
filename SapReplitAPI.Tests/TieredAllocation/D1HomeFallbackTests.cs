using SapReplitAPI.Models.ZoneFulfillment;
using SapReplitAPI.Services.ZoneFulfillment;
using Xunit;

namespace SapReplitAPI.Tests.TieredAllocation;

/// <summary>
/// D1 HOME-ZONE-FIRST invariant tests.
/// All scenarios exercise the strict home/fallback separation:
///   - Tier 1 and Tier 2 are HOME-ONLY.
///   - Fallback enters only at Tier 3 (whole-line) or Tier 4 (split).
///   - Empty fallback degrades gracefully to Tier 4/5 without throwing.
/// Engine is pure (no I/O, no DI). All inputs are in-memory.
/// </summary>
public sealed class D1HomeFallbackTests
{
    private static readonly TieredZoneAllocationEngine Engine = new();

    private static ZoneWarehouse W(string code, int pri) => new(code, pri);
    private static DomainRequestLine Line(string item, decimal qty, int seq = 1)
        => new(Guid.NewGuid(), seq, item, qty, 10m, null, null, null);
    private static OitwSnapshot Stock(string item, string whs, decimal avail)
        => new(item, whs, avail);

    // ── D101 ─────────────────────────────────────────────────────────────────
    // FALLBACK has more stock than HOME but HOME qualifies for Tier 1 → HOME wins.
    // Critical D1 invariant: fallback cannot win Tier 1 even with better stock.
    [Fact]
    public void D101_Tier1_HomePicked_EvenWhenFallbackHasMoreStock()
    {
        var home     = new[] { W("004", 1) };
        var fallback = new[] { W("003", 2) };
        var line     = Line("ITEM-A", 5);
        var stocks   = new[]
        {
            Stock("ITEM-A", "004", 10),  // HOME: 10 units
            Stock("ITEM-A", "003", 50)   // FALLBACK: 50 units — should not win
        };

        var result = Engine.Allocate(home, fallback, new[] { line }, stocks);

        Assert.Equal(1, result.AllocationTier);
        Assert.All(result.BaseResult.Fragments, f => Assert.Equal("004", f.WhsCode));
        Assert.False(result.BaseResult.HasShortage);
    }

    // ── D102 ─────────────────────────────────────────────────────────────────
    // FALLBACK has full basket; HOME does not → Tier 1 skipped, Tier 2 attempted on HOME.
    // Fallback cannot win Tier 1 even when home cannot supply the basket.
    [Fact]
    public void D102_Tier1_Skipped_WhenOnlyFallbackHasFullBasket()
    {
        var home     = new[] { W("004", 1), W("001", 2) };
        var fallback = new[] { W("003", 3) };
        var line     = Line("ITEM-A", 10);
        var stocks   = new[]
        {
            Stock("ITEM-A", "004", 3),   // HOME: insufficient
            Stock("ITEM-A", "001", 4),   // HOME: insufficient
            Stock("ITEM-A", "003", 20)   // FALLBACK: sufficient but must not win Tier 1
        };

        var result = Engine.Allocate(home, fallback, new[] { line }, stocks);

        // Tier 1 not achieved; result must be Tier ≥ 3 (fallback whole-line for non-home-coverable)
        Assert.True(result.AllocationTier >= 3);
        // No fragment should come from 003 via Tier 1
        Assert.DoesNotContain(result.BaseResult.Fragments,
            f => f.WhsCode == "003" && result.AllocationTier == 1);
    }

    // ── D103 ─────────────────────────────────────────────────────────────────
    // HOME has a minimum 2-WHS subset covering all lines whole → Tier 2 from HOME only.
    // FALLBACK warehouses are not included in the Tier 2 subset search.
    [Fact]
    public void D103_Tier2_UsesHomeOnly_FallbackNotInSubset()
    {
        var home     = new[] { W("004", 1), W("001", 2) };
        var fallback = new[] { W("003", 3) };
        var lA = Line("ITEM-A", 5, 1);
        var lB = Line("ITEM-B", 5, 2);
        var stocks = new[]
        {
            Stock("ITEM-A", "004", 10),
            Stock("ITEM-B", "001", 10),
            Stock("ITEM-A", "003", 10),  // FALLBACK also has stock — must not appear in Tier 2
            Stock("ITEM-B", "003", 10)
        };

        var result = Engine.Allocate(home, fallback, new[] { lA, lB }, stocks);

        Assert.Equal(2, result.AllocationTier);
        Assert.DoesNotContain(result.BaseResult.Fragments, f => f.WhsCode == "003");
        Assert.False(result.BaseResult.HasShortage);
    }

    // ── D104 ─────────────────────────────────────────────────────────────────
    // Tier 2: FALLBACK cannot win even when it is the only WHS with a single subset.
    // Critical D1 invariant: Tier 2 must fail if only fallback can cover all lines.
    [Fact]
    public void D104_Tier2_FallbackCannotWin_EvenIfOnlyFallbackCoversAll()
    {
        var home     = new[] { W("004", 1) };
        var fallback = new[] { W("003", 2) };
        var lA = Line("ITEM-A", 5, 1);
        var lB = Line("ITEM-B", 5, 2);
        var stocks = new[]
        {
            Stock("ITEM-A", "003", 10),  // FALLBACK has both items
            Stock("ITEM-B", "003", 10),
            // HOME has nothing
        };

        var result = Engine.Allocate(home, fallback, new[] { lA, lB }, stocks);

        // Tier 2 must NOT be used; result is Tier 3 (fallback whole-line for non-home-coverable lines)
        Assert.NotEqual(2, result.AllocationTier);
        // Fragments come from fallback (003) via Tier 3
        Assert.All(result.BaseResult.Fragments, f => Assert.Equal("003", f.WhsCode));
        Assert.Equal(3, result.AllocationTier);
    }

    // ── D105 ─────────────────────────────────────────────────────────────────
    // Phase A: correctly identifies home-coverable vs non-home-coverable lines.
    // Line with qty ≤ home WHS avail → home-coverable.
    // Line with qty > all home WHS avail but ≤ fallback avail → NOT home-coverable.
    [Fact]
    public void D105_PhaseA_ClassifiesLinesCorrectly()
    {
        var home     = new[] { W("004", 1) };
        var fallback = new[] { W("003", 2) };
        var lHome    = Line("ITEM-A", 5, 1);  // HOME has 10 → home-coverable
        var lFall    = Line("ITEM-B", 8, 2);  // HOME has 3, FALLBACK has 20 → NOT home-coverable
        var stocks   = new[]
        {
            Stock("ITEM-A", "004", 10),
            Stock("ITEM-B", "004", 3),   // insufficient for lFall
            Stock("ITEM-B", "003", 20)   // fallback covers lFall
        };

        var result = Engine.Allocate(home, fallback, new[] { lHome, lFall }, stocks);

        // lHome goes to home (004), lFall goes to fallback (003)
        var fragHome = result.BaseResult.Fragments.Single(f => f.RequestLineId == lHome.RequestLineId);
        var fragFall = result.BaseResult.Fragments.Single(f => f.RequestLineId == lFall.RequestLineId);

        Assert.Equal("004", fragHome.WhsCode);
        Assert.Equal("003", fragFall.WhsCode);
        Assert.False(result.BaseResult.HasShortage);
        // worst tier = 3 (fallback used for lFall)
        Assert.Equal(3, result.AllocationTier);
    }

    // ── D106 ─────────────────────────────────────────────────────────────────
    // Phase B: minimum HOME subset allocates home-coverable lines (SourceTier = 2).
    // In a Tier-3 context, home lines get SourceTier=2; overall AllocationTier=3.
    [Fact]
    public void D106_PhaseB_HomeCoverableLines_GetSourceTier2_InTier3Context()
    {
        var home     = new[] { W("004", 1), W("001", 2) };
        var fallback = new[] { W("003", 3) };
        var lHome    = Line("ITEM-A", 5, 1);  // home-coverable
        var lFall    = Line("ITEM-B", 8, 2);  // not home-coverable (004.B=3, 001.B=2; fallback.B=20)
        var stocks   = new[]
        {
            Stock("ITEM-A", "004", 10),
            Stock("ITEM-B", "004", 3),
            Stock("ITEM-B", "001", 2),
            Stock("ITEM-B", "003", 20)
        };

        var result = Engine.Allocate(home, fallback, new[] { lHome, lFall }, stocks);

        var homeTier = result.FragmentTiers.Single(ft => ft.RequestLineId == lHome.RequestLineId);
        var fallTier = result.FragmentTiers.Single(ft => ft.RequestLineId == lFall.RequestLineId);

        Assert.Equal(2, homeTier.SourceTier);   // Phase B = SourceTier 2
        Assert.Equal(3, fallTier.SourceTier);   // Fallback whole-line = SourceTier 3
        Assert.Equal(3, result.AllocationTier); // overall worst = 3
    }

    // ── D107 ─────────────────────────────────────────────────────────────────
    // Phase C: non-home-coverable line uses FALLBACK whole-line, first qualifying WHS.
    [Fact]
    public void D107_PhaseC_FallbackWholeLineForNonHomeCoverableLine()
    {
        var home     = new[] { W("004", 1) };
        var fallback = new[] { W("001", 2), W("003", 3) };  // 001 first in fallback order
        var line     = Line("ITEM-A", 10);                   // HOME.A=3 → not home-coverable
        var stocks   = new[]
        {
            Stock("ITEM-A", "004", 3),
            Stock("ITEM-A", "001", 15),  // first fallback WHS with full qty
            Stock("ITEM-A", "003", 20)
        };

        var result = Engine.Allocate(home, fallback, new[] { line }, stocks);

        Assert.Equal(3, result.AllocationTier);
        var frag = result.BaseResult.Fragments.Single();
        Assert.Equal("001", frag.WhsCode);  // first fallback WHS wins
        Assert.Equal(10m, frag.AllocatedQty);
        Assert.False(result.BaseResult.HasShortage);
    }

    // ── D108 ─────────────────────────────────────────────────────────────────
    // Phase C → Phase D: non-home-coverable line with no whole-line FALLBACK goes to Tier 4 split.
    [Fact]
    public void D108_PhaseD_NonHomeCoverable_NoFallbackWhole_GoesToTier4Split()
    {
        var home     = new[] { W("004", 1) };
        var fallback = new[] { W("003", 2) };
        var line     = Line("ITEM-A", 10);
        var stocks   = new[]
        {
            Stock("ITEM-A", "004", 3),  // HOME: insufficient
            Stock("ITEM-A", "003", 4),  // FALLBACK: insufficient whole, but contributes to split
        };

        var result = Engine.Allocate(home, fallback, new[] { line }, stocks);

        // Combined=7 < 10 → Tier 5 shortage
        Assert.Equal(5, result.AllocationTier);
        Assert.True(result.BaseResult.HasShortage);
    }

    // ── D109 ─────────────────────────────────────────────────────────────────
    // Tier 4 split: HOME cascade first, then FALLBACK cascade.
    // Home fragments appear before fallback fragments in the cascade.
    [Fact]
    public void D109_Tier4_HomeCascadeFirst_ThenFallbackCascade()
    {
        var home     = new[] { W("004", 1), W("001", 2) };
        var fallback = new[] { W("003", 3) };
        var line     = Line("ITEM-A", 20);
        var stocks   = new[]
        {
            Stock("ITEM-A", "004", 6),   // HOME1
            Stock("ITEM-A", "001", 7),   // HOME2
            Stock("ITEM-A", "003", 10)   // FALLBACK
        };

        var result = Engine.Allocate(home, fallback, new[] { line }, stocks);

        // Combined = 6+7+10 = 23 ≥ 20 → no shortage
        Assert.Equal(4, result.AllocationTier);
        Assert.False(result.BaseResult.HasShortage);

        // Verify HomeWHS appear and contribute first
        var f004 = result.BaseResult.Fragments.FirstOrDefault(f => f.WhsCode == "004");
        var f001 = result.BaseResult.Fragments.FirstOrDefault(f => f.WhsCode == "001");
        var f003 = result.BaseResult.Fragments.FirstOrDefault(f => f.WhsCode == "003");

        Assert.NotNull(f004); Assert.Equal(6m,  f004!.AllocatedQty);
        Assert.NotNull(f001); Assert.Equal(7m,  f001!.AllocatedQty);
        Assert.NotNull(f003); Assert.Equal(7m,  f003!.AllocatedQty); // 20-6-7=7 from fallback
    }

    // ── D110 ─────────────────────────────────────────────────────────────────
    // Tier 5: HOME + FALLBACK combined stock still insufficient → HasShortage=true.
    [Fact]
    public void D110_Tier5_HomeAndFallbackCombinedInsufficient_HasShortage()
    {
        var home     = new[] { W("004", 1) };
        var fallback = new[] { W("003", 2) };
        var line     = Line("ITEM-A", 20);
        var stocks   = new[]
        {
            Stock("ITEM-A", "004", 3),
            Stock("ITEM-A", "003", 4)
        };

        var result = Engine.Allocate(home, fallback, new[] { line }, stocks);

        Assert.Equal(5, result.AllocationTier);
        Assert.True(result.BaseResult.HasShortage);
    }

    // ── D111 ─────────────────────────────────────────────────────────────────
    // A1-equivalent: when all 4 WHS are in HOME (empty fallback), Tier 1 from best home WHS.
    [Fact]
    public void D111_A1AllFourWhsAsHome_Tier1_BestHomeWins()
    {
        // origin=003 cluster: all four warehouses are HOME, FALLBACK empty.
        var home = new[] { W("003", 1), W("004", 2), W("001", 3), W("002", 4) };
        var line = Line("ITEM-A", 5);
        var stocks = new[]
        {
            Stock("ITEM-A", "003", 10),
            Stock("ITEM-A", "004", 8)
        };

        var result = Engine.Allocate(home, Array.Empty<ZoneWarehouse>(), new[] { line }, stocks);

        Assert.Equal(1, result.AllocationTier);
        Assert.All(result.BaseResult.Fragments, f => Assert.Equal("003", f.WhsCode)); // first HOME wins
        Assert.False(result.BaseResult.HasShortage);
    }

    // ── D112 ─────────────────────────────────────────────────────────────────
    // Mikocheni pattern: HOME=[003], FALLBACK=[001,002,004].
    // Home-coverable lines use 003; non-home-coverable use first fallback WHS with full qty.
    [Fact]
    public void D112_MikocheniPattern_Home003Only_FallbackForNonHomeLine()
    {
        var home     = new[] { W("003", 1) };
        var fallback = new[] { W("004", 2), W("001", 3), W("002", 4) };
        var lHome    = Line("ITEM-A", 5, 1);  // 003.A=10 → home-coverable
        var lFall    = Line("ITEM-B", 8, 2);  // 003.B=2 → not home-coverable
        var stocks   = new[]
        {
            Stock("ITEM-A", "003", 10),
            Stock("ITEM-B", "003", 2),
            Stock("ITEM-B", "004", 15)  // first fallback with full qty
        };

        var result = Engine.Allocate(home, fallback, new[] { lHome, lFall }, stocks);

        var fragA = result.BaseResult.Fragments.Single(f => f.RequestLineId == lHome.RequestLineId);
        var fragB = result.BaseResult.Fragments.Single(f => f.RequestLineId == lFall.RequestLineId);

        Assert.Equal("003", fragA.WhsCode); // home
        Assert.Equal("004", fragB.WhsCode); // fallback
        Assert.Equal(3, result.AllocationTier);
        Assert.False(result.BaseResult.HasShortage);
    }

    // ── D113 ─────────────────────────────────────────────────────────────────
    // Empty FALLBACK: non-home-coverable line falls directly to Tier 4 cascade (HOME only).
    [Fact]
    public void D113_EmptyFallback_NonHomeCoverable_GoesToTier4HomeOnly()
    {
        var home     = new[] { W("004", 1), W("001", 2) };
        // FALLBACK intentionally empty (e.g. A1 special case)
        var line     = Line("ITEM-A", 10); // HOME combined has 12 but no single WHS has 10
        var stocks   = new[]
        {
            Stock("ITEM-A", "004", 6),
            Stock("ITEM-A", "001", 6)
        };

        var result = Engine.Allocate(home, Array.Empty<ZoneWarehouse>(), new[] { line }, stocks);

        Assert.Equal(4, result.AllocationTier);
        Assert.False(result.BaseResult.HasShortage);
        // Split across 004 and 001
        Assert.True(result.BaseResult.Fragments.Count >= 2);
    }

    // ── D114 ─────────────────────────────────────────────────────────────────
    // FragmentTiers: home Tier 1 fragments all have SourceTier = 1.
    [Fact]
    public void D114_FragmentTiers_Tier1_AllSourceTier1()
    {
        var home     = new[] { W("004", 1), W("001", 2) };
        var fallback = new[] { W("003", 3) };
        var lA = Line("ITEM-A", 5, 1);
        var lB = Line("ITEM-B", 3, 2);
        var stocks = new[]
        {
            Stock("ITEM-A", "004", 10),
            Stock("ITEM-B", "004", 10)
        };

        var result = Engine.Allocate(home, fallback, new[] { lA, lB }, stocks);

        Assert.Equal(1, result.AllocationTier);
        Assert.All(result.FragmentTiers, ft => Assert.Equal(1, ft.SourceTier));
    }

    // ── D115 ─────────────────────────────────────────────────────────────────
    // FragmentTiers: Tier 3 fallback fragment has SourceTier = 3; home fragment SourceTier = 2.
    [Fact]
    public void D115_FragmentTiers_Tier3_MixedSourceTiers()
    {
        var home     = new[] { W("004", 1) };
        var fallback = new[] { W("003", 2) };
        var lHome    = Line("ITEM-A", 5, 1);
        var lFall    = Line("ITEM-B", 8, 2);
        var stocks   = new[]
        {
            Stock("ITEM-A", "004", 10),
            Stock("ITEM-B", "004", 2),   // HOME insufficient for lFall
            Stock("ITEM-B", "003", 15)
        };

        var result = Engine.Allocate(home, fallback, new[] { lHome, lFall }, stocks);

        var homeFt = result.FragmentTiers.Single(ft => ft.RequestLineId == lHome.RequestLineId);
        var fallFt = result.FragmentTiers.Single(ft => ft.RequestLineId == lFall.RequestLineId);

        Assert.Equal(2, homeFt.SourceTier);  // Phase B minimum home set
        Assert.Equal(3, fallFt.SourceTier);  // Fallback whole-line
        Assert.Equal(3, result.AllocationTier);
    }

    // ── D116 ─────────────────────────────────────────────────────────────────
    // AllocationTier = max SourceTier across all fragments (worst tier wins).
    [Fact]
    public void D116_AllocationTier_IsMaxAcrossFragmentTiers()
    {
        var home     = new[] { W("004", 1) };
        var fallback = new[] { W("003", 2) };
        var lHome    = Line("ITEM-A", 5, 1);  // home-coverable → SourceTier=2
        var lFall    = Line("ITEM-B", 8, 2);  // fallback whole → SourceTier=3
        var lShort   = Line("ITEM-C", 20, 3); // insufficient everywhere → SourceTier=5
        var stocks   = new[]
        {
            Stock("ITEM-A", "004", 10),
            Stock("ITEM-B", "003", 15),
            Stock("ITEM-C", "004", 1),
            Stock("ITEM-C", "003", 1)
        };

        var result = Engine.Allocate(home, fallback, new[] { lHome, lFall, lShort }, stocks);

        Assert.Equal(5, result.AllocationTier);
        Assert.True(result.BaseResult.HasShortage);
    }

    // ── D117 ─────────────────────────────────────────────────────────────────
    // Empty HOME guard: engine throws InvalidOperationException when HomeZone is empty.
    [Fact]
    public void D117_EmptyHomeZone_ThrowsInvalidOperationException()
    {
        var fallback = new[] { W("003", 1) };
        var line     = Line("ITEM-A", 5);
        var stocks   = new[] { Stock("ITEM-A", "003", 10) };

        var ex = Assert.Throws<InvalidOperationException>(
            () => Engine.Allocate(Array.Empty<ZoneWarehouse>(), fallback, new[] { line }, stocks));

        Assert.Contains("HomeZone", ex.Message);
    }

    // ── D118 ─────────────────────────────────────────────────────────────────
    // Tier 2 minimum-set tie-breaking with home+fallback: home subset {004,001} beats
    // a hypothetical fallback set — but fallback is never in the Tier 2 search at all.
    // This verifies that adding large fallback stock does not change Tier 2 selection.
    [Fact]
    public void D118_Tier2_FallbackStockIgnored_HomeSubsetUnchanged()
    {
        var home     = new[] { W("004", 1), W("001", 2) };
        var fallback = new[] { W("003", 3) };
        var lA = Line("ITEM-A", 5, 1);
        var lB = Line("ITEM-B", 5, 2);
        var stocks = new[]
        {
            Stock("ITEM-A", "004", 10),
            Stock("ITEM-B", "001", 10),
            // Give fallback abundant stock — should not affect Tier 2 selection
            Stock("ITEM-A", "003", 100),
            Stock("ITEM-B", "003", 100)
        };

        var result = Engine.Allocate(home, fallback, new[] { lA, lB }, stocks);

        Assert.Equal(2, result.AllocationTier);
        // No fragment from fallback WHS 003
        Assert.DoesNotContain(result.BaseResult.Fragments, f => f.WhsCode == "003");
    }
}
