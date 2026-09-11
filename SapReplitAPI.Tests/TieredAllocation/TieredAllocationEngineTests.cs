using SapReplitAPI.Models.ZoneFulfillment;
using SapReplitAPI.Services.ZoneFulfillment;
using Xunit;

namespace SapReplitAPI.Tests.TieredAllocation;

/// <summary>
/// Pure unit tests for TieredZoneAllocationEngine — no I/O, no DI, no SAP.
/// All inputs are constructed in-memory; results are deterministic.
/// </summary>
public sealed class TieredAllocationEngineTests
{
    private static readonly TieredZoneAllocationEngine Engine = new();

    // ── helpers ──────────────────────────────────────────────────────────────

    private static ZoneWarehouse W(string code, int pri) => new(code, pri);

    private static DomainRequestLine Line(string item, decimal qty, int seq = 1)
        => new(Guid.NewGuid(), seq, item, qty, 10m, null, null, null);

    private static OitwSnapshot Stock(string item, string whs, decimal avail)
        => new(item, whs, avail);

    // ── T01 ──────────────────────────────────────────────────────────────────
    // Cluster origin004: zone=[004,001,002,003]; both 004 and 001 have full basket → 004 wins.
    // D1: fallback warehouse cannot participate in Tier1/Tier2
    [Fact]
    public void T01_Tier1_Origin004_BothWhsHaveFullBasket_First004Wins()
    {
        var zone = new[] { W("004", 1), W("001", 2), W("002", 3), W("003", 4) };
        var lines = new[] { Line("ITEM-A", 5) };
        var stocks = new[]
        {
            Stock("ITEM-A", "004", 10),
            Stock("ITEM-A", "001", 10)
        };

        var result = Engine.Allocate(zone, Array.Empty<ZoneWarehouse>(), lines, stocks);

        Assert.Equal(1, result.AllocationTier);
        Assert.All(result.BaseResult.Fragments, f => Assert.Equal("004", f.WhsCode));
        Assert.False(result.BaseResult.HasShortage);
    }

    // ── T02 ──────────────────────────────────────────────────────────────────
    // Cluster origin001: zone=[001,002,004,003]; only 001 has full basket → Tier 1, 001.
    // D1: fallback warehouse cannot participate in Tier1/Tier2
    [Fact]
    public void T02_Tier1_Origin001_Only001HasFullBasket()
    {
        var zone  = new[] { W("001", 1), W("002", 2), W("004", 3), W("003", 4) };
        var lines = new[] { Line("ITEM-A", 5) };
        var stocks = new[]
        {
            Stock("ITEM-A", "001", 10),
            Stock("ITEM-A", "002", 2)    // insufficient
        };

        var result = Engine.Allocate(zone, Array.Empty<ZoneWarehouse>(), lines, stocks);

        Assert.Equal(1, result.AllocationTier);
        Assert.All(result.BaseResult.Fragments, f => Assert.Equal("001", f.WhsCode));
    }

    // ── T03 ──────────────────────────────────────────────────────────────────
    // Single WHS, full basket → Tier 1.
    [Fact]
    public void T03_Tier1_SingleWhs_FullBasket()
    {
        var zone  = new[] { W("001", 1) };
        var lines = new[] { Line("ITEM-A", 3) };
        var stocks = new[] { Stock("ITEM-A", "001", 10) };

        var result = Engine.Allocate(zone, Array.Empty<ZoneWarehouse>(), lines, stocks);

        Assert.Equal(1, result.AllocationTier);
        Assert.Single(result.BaseResult.Fragments);
        Assert.Equal("001", result.BaseResult.Fragments[0].WhsCode);
    }

    // ── T04 ──────────────────────────────────────────────────────────────────
    // Tier 1: all fragments sourced exclusively from the chosen WHS; none from others.
    [Fact]
    public void T04_Tier1_AllFragmentsFromSingleWhs_NoOtherWhs()
    {
        var zone  = new[] { W("001", 1), W("002", 2) };
        var lineA = Line("ITEM-A", 2, 1);
        var lineB = Line("ITEM-B", 3, 2);
        var stocks = new[]
        {
            Stock("ITEM-A", "001", 10), Stock("ITEM-B", "001", 10),
            Stock("ITEM-A", "002", 10), Stock("ITEM-B", "002", 10)
        };

        var result = Engine.Allocate(zone, Array.Empty<ZoneWarehouse>(), new[] { lineA, lineB }, stocks);

        Assert.Equal(1, result.AllocationTier);
        // First WHS (001) wins; no fragment should be from 002
        Assert.DoesNotContain(result.BaseResult.Fragments, f => f.WhsCode == "002");
    }

    // ── T05 ──────────────────────────────────────────────────────────────────
    // Tier 2: item A only in 001, item B only in 002 → minimum set = {001,002}.
    [Fact]
    public void T05_Tier2_TwoItemsExclusiveToTwoWhs_MinimumSetSize2()
    {
        var zone  = new[] { W("001", 1), W("002", 2) };
        var lineA = Line("ITEM-A", 5, 1);
        var lineB = Line("ITEM-B", 5, 2);
        var stocks = new[]
        {
            Stock("ITEM-A", "001", 10),
            Stock("ITEM-B", "002", 10)
        };

        var result = Engine.Allocate(zone, Array.Empty<ZoneWarehouse>(), new[] { lineA, lineB }, stocks);

        Assert.Equal(2, result.AllocationTier);
        Assert.Contains(result.BaseResult.Fragments, f => f.WhsCode == "001");
        Assert.Contains(result.BaseResult.Fragments, f => f.WhsCode == "002");
        Assert.False(result.BaseResult.HasShortage);
        Assert.Contains("Tier2", result.AllocationReason);
    }

    // ── T06 ──────────────────────────────────────────────────────────────────
    // Tier 2: 3-WHS zone, minimum set is still size 2.
    // D1: fallback warehouse cannot participate in Tier1/Tier2
    [Fact]
    public void T06_Tier2_ThreeWhsZone_MinimumSetIsStillTwo()
    {
        var zone  = new[] { W("001", 1), W("002", 2), W("003", 3) };
        var lineA = Line("ITEM-A", 5, 1);
        var lineB = Line("ITEM-B", 5, 2);
        var stocks = new[]
        {
            Stock("ITEM-A", "001", 10),
            Stock("ITEM-B", "002", 10)
            // 003 has nothing
        };

        var result = Engine.Allocate(zone, Array.Empty<ZoneWarehouse>(), new[] { lineA, lineB }, stocks);

        Assert.Equal(2, result.AllocationTier);
        // No fragment should come from 003
        Assert.DoesNotContain(result.BaseResult.Fragments, f => f.WhsCode == "003");
    }

    // ── T07 ──────────────────────────────────────────────────────────────────
    // Tier 2 tie-breaking: two equal-cardinality subsets; lexicographic priority-index
    // vector selects {001,002} over {003,004}.
    // D1: fallback warehouse cannot participate in Tier1/Tier2
    [Fact]
    public void T07_Tier2_TieBreaking_LowerPriorityVectorWins()
    {
        // Zone priority: 001(0), 002(1), 003(2), 004(3)
        var zone = new[] { W("001", 1), W("002", 2), W("003", 3), W("004", 4) };

        // Items A,B only in 001; C,D only in 002.
        // Items A,B also in 003; C,D also in 004.
        // Both {001,002} and {003,004} can supply all lines whole with size 2.
        // Vector [0,1] < [2,3] → {001,002} wins.
        var lA = Line("ITEM-A", 5, 1);
        var lB = Line("ITEM-B", 5, 2);
        var lC = Line("ITEM-C", 5, 3);
        var lD = Line("ITEM-D", 5, 4);
        var stocks = new[]
        {
            Stock("ITEM-A", "001", 10), Stock("ITEM-B", "001", 10),
            Stock("ITEM-C", "002", 10), Stock("ITEM-D", "002", 10),
            Stock("ITEM-A", "003", 10), Stock("ITEM-B", "003", 10),
            Stock("ITEM-C", "004", 10), Stock("ITEM-D", "004", 10)
        };

        var result = Engine.Allocate(zone, Array.Empty<ZoneWarehouse>(), new[] { lA, lB, lC, lD }, stocks);

        Assert.Equal(2, result.AllocationTier);
        // 001 and 002 win; 003 and 004 should not appear
        Assert.DoesNotContain(result.BaseResult.Fragments, f => f.WhsCode == "003");
        Assert.DoesNotContain(result.BaseResult.Fragments, f => f.WhsCode == "004");
        Assert.Contains(result.BaseResult.Fragments, f => f.WhsCode == "001");
        Assert.Contains(result.BaseResult.Fragments, f => f.WhsCode == "002");
    }

    // ── T08 ──────────────────────────────────────────────────────────────────
    // Both lines are individually home-coverable but compete for the same item stock.
    // Phase B can't find a min-set covering both → both fall to Phase D (Tier 4 split).
    // Total stock = 11; Line1 takes 5 from 001 (001.A→3); Line2 splits: 001.A=3+002.A=2.
    [Fact]
    public void T08_MixedTier4_CompetingHomeCoverableLines_BothSplit()
    {
        var zone = new[] { W("001", 1), W("002", 2) };
        var l1 = Line("ITEM-A", 5, 1);
        var l2 = Line("ITEM-A", 5, 2);
        var stocks = new[]
        {
            Stock("ITEM-A", "001", 8),
            Stock("ITEM-A", "002", 3)
        };

        var result = Engine.Allocate(zone, Array.Empty<ZoneWarehouse>(), new[] { l1, l2 }, stocks);

        Assert.Equal(4, result.AllocationTier);
        Assert.False(result.BaseResult.HasShortage);
        // Line2 should have fragments from both WHS
        var l2Frags = result.BaseResult.Fragments.Where(f => f.RequestLineId == l2.RequestLineId).ToList();
        Assert.True(l2Frags.Count >= 2, "Line2 should be split across multiple WHS");
    }

    // ── T09 ──────────────────────────────────────────────────────────────────
    // Tier 4: single line split across two WHS, combined stock sufficient (no shortage).
    [Fact]
    public void T09_Tier4_SingleLineSplit_NoShortage()
    {
        var zone = new[] { W("001", 1), W("002", 2) };
        var line = Line("ITEM-A", 5);
        var stocks = new[]
        {
            Stock("ITEM-A", "001", 3),
            Stock("ITEM-A", "002", 4)
        };

        var result = Engine.Allocate(zone, Array.Empty<ZoneWarehouse>(), new[] { line }, stocks);

        Assert.Equal(4, result.AllocationTier);
        Assert.False(result.BaseResult.HasShortage);
        var frags = result.BaseResult.Fragments;
        // 3 from 001, 2 from 002
        var f001 = frags.First(f => f.WhsCode == "001");
        var f002 = frags.First(f => f.WhsCode == "002");
        Assert.Equal(3m, f001.AllocatedQty);
        Assert.Equal(2m, f002.AllocatedQty);
    }

    // ── T10 ──────────────────────────────────────────────────────────────────
    // Tier 5: combined stock insufficient → HasShortage=true.
    [Fact]
    public void T10_Tier5_InsufficientCombinedStock_HasShortage()
    {
        var zone = new[] { W("001", 1), W("002", 2) };
        var line = Line("ITEM-A", 5);
        var stocks = new[]
        {
            Stock("ITEM-A", "001", 2),
            Stock("ITEM-A", "002", 1)
        };

        var result = Engine.Allocate(zone, Array.Empty<ZoneWarehouse>(), new[] { line }, stocks);

        Assert.Equal(5, result.AllocationTier);
        Assert.True(result.BaseResult.HasShortage);
    }

    // ── T11 ──────────────────────────────────────────────────────────────────
    // AllocationTier = highest (worst) tier across all lines.
    // D1: Line1 (ITEM-A) home-coverable → Phase B allocates from 001 (SourceTier=2).
    // Line2 (ITEM-B) not home-coverable → no fallback → Tier 4/5 split with shortage.
    // Overall = Tier 5.
    [Fact]
    public void T11_AllocationTier_IsWorstAcrossLines()
    {
        var zone = new[] { W("001", 1), W("002", 2) };
        var l1 = Line("ITEM-A", 5, 1);
        var l2 = Line("ITEM-B", 3, 2);
        var stocks = new[]
        {
            Stock("ITEM-A", "001", 5),   // Line1: home-coverable, Phase B from 001
            Stock("ITEM-B", "001", 1),   // Line2: not home-coverable; combined=2 < 3 → Tier 5
            Stock("ITEM-B", "002", 1)
        };

        var result = Engine.Allocate(zone, Array.Empty<ZoneWarehouse>(), new[] { l1, l2 }, stocks);

        Assert.Equal(5, result.AllocationTier);
        Assert.True(result.BaseResult.HasShortage);
    }

    // ── T12 ──────────────────────────────────────────────────────────────────
    // Tier 1 result has AllocationTier = 1 in FragmentTiers for all fragments.
    [Fact]
    public void T12_Tier1_AllFragmentTiersAre1()
    {
        var zone  = new[] { W("001", 1), W("002", 2) };
        var line  = Line("ITEM-A", 5);
        var stocks = new[] { Stock("ITEM-A", "001", 10) };

        var result = Engine.Allocate(zone, Array.Empty<ZoneWarehouse>(), new[] { line }, stocks);

        Assert.Equal(1, result.AllocationTier);
        Assert.All(result.FragmentTiers, ft => Assert.Equal(1, ft.SourceTier));
    }

    // ── T13 ──────────────────────────────────────────────────────────────────
    // Tier 1 AllocationReason contains "Tier1".
    [Fact]
    public void T13_Tier1_AllocationReasonContainsTier1()
    {
        var zone  = new[] { W("001", 1) };
        var line  = Line("ITEM-A", 2);
        var stocks = new[] { Stock("ITEM-A", "001", 10) };

        var result = Engine.Allocate(zone, Array.Empty<ZoneWarehouse>(), new[] { line }, stocks);

        Assert.Contains("Tier1", result.AllocationReason, StringComparison.OrdinalIgnoreCase);
    }

    // ── T14 ──────────────────────────────────────────────────────────────────
    // Tier 5: AllocationTier = 5 whenever any line has shortage.
    [Fact]
    public void T14_Tier5_AllocationTierIs5WhenAnyLineHasShortage()
    {
        var zone = new[] { W("001", 1) };
        var line = Line("ITEM-A", 10);
        var stocks = new[] { Stock("ITEM-A", "001", 0) };

        var result = Engine.Allocate(zone, Array.Empty<ZoneWarehouse>(), new[] { line }, stocks);

        Assert.Equal(5, result.AllocationTier);
        Assert.True(result.BaseResult.HasShortage);
    }

    // ── T15 ──────────────────────────────────────────────────────────────────
    // Tier 5 shortage fragment: AllocatedQty = 0, UnallocatedQty = RequestedQty, HasShortage = true.
    [Fact]
    public void T15_Tier5_ShortageFragment_HasCorrectQtys()
    {
        var zone  = new[] { W("001", 1) };
        var line  = Line("ITEM-A", 7);
        var stocks = new[] { Stock("ITEM-A", "001", 0) };

        var result = Engine.Allocate(zone, Array.Empty<ZoneWarehouse>(), new[] { line }, stocks);

        var frag = result.BaseResult.Fragments.Single(f => f.RequestLineId == line.RequestLineId);
        Assert.Equal(0m, frag.AllocatedQty);
        Assert.Equal(7m, frag.UnallocatedQty);
        Assert.True(frag.HasShortage);
    }

    // ── T16 ──────────────────────────────────────────────────────────────────
    // Multi-item Tier 1: origin004 zone, WHS004 has full basket of two items → Tier 1.
    // D1: fallback warehouse cannot participate in Tier1/Tier2
    [Fact]
    public void T16_Tier1_MultiItem_Origin004Zone_004HasFullBasket()
    {
        var zone  = new[] { W("004", 1), W("001", 2), W("002", 3), W("003", 4) };
        var lA    = Line("ITEM-A", 5, 1);
        var lB    = Line("ITEM-B", 3, 2);
        var stocks = new[]
        {
            Stock("ITEM-A", "004", 10), Stock("ITEM-B", "004", 10)
        };

        var result = Engine.Allocate(zone, Array.Empty<ZoneWarehouse>(), new[] { lA, lB }, stocks);

        Assert.Equal(1, result.AllocationTier);
        Assert.All(result.BaseResult.Fragments, f => Assert.Equal("004", f.WhsCode));
    }

    // ── T17 ──────────────────────────────────────────────────────────────────
    // Tier 2 reason string contains "Tier2".
    [Fact]
    public void T17_Tier2_AllocationReasonContainsTier2()
    {
        var zone  = new[] { W("001", 1), W("002", 2) };
        var lA    = Line("ITEM-A", 5, 1);
        var lB    = Line("ITEM-B", 5, 2);
        var stocks = new[]
        {
            Stock("ITEM-A", "001", 10),
            Stock("ITEM-B", "002", 10)
        };

        var result = Engine.Allocate(zone, Array.Empty<ZoneWarehouse>(), new[] { lA, lB }, stocks);

        Assert.Equal(2, result.AllocationTier);
        Assert.Contains("Tier2", result.AllocationReason, StringComparison.OrdinalIgnoreCase);
    }

    // ── T18 ──────────────────────────────────────────────────────────────────
    // Tier 2 FragmentTiers: all SourceTier values are 2.
    [Fact]
    public void T18_Tier2_AllFragmentTiersAre2()
    {
        var zone  = new[] { W("001", 1), W("002", 2) };
        var lA    = Line("ITEM-A", 5, 1);
        var lB    = Line("ITEM-B", 5, 2);
        var stocks = new[]
        {
            Stock("ITEM-A", "001", 10),
            Stock("ITEM-B", "002", 10)
        };

        var result = Engine.Allocate(zone, Array.Empty<ZoneWarehouse>(), new[] { lA, lB }, stocks);

        Assert.Equal(2, result.AllocationTier);
        Assert.All(result.FragmentTiers, ft => Assert.Equal(2, ft.SourceTier));
    }

    // ── T19 ──────────────────────────────────────────────────────────────────
    // Tier 2 with 4-WHS zone: tie-breaking verified; {001,002} beats {003,004}.
    // Fragment count = number of lines (2); each assigned a distinct WHS.
    // D1: fallback warehouse cannot participate in Tier1/Tier2
    [Fact]
    public void T19_Tier2_FourWhsZone_TieBroken_FragmentCountEqualsLines()
    {
        var zone = new[] { W("001", 1), W("002", 2), W("003", 3), W("004", 4) };
        var lA   = Line("ITEM-A", 5, 1);
        var lB   = Line("ITEM-B", 5, 2);
        var stocks = new[]
        {
            Stock("ITEM-A", "001", 10),
            Stock("ITEM-B", "002", 10),
            Stock("ITEM-A", "003", 10),
            Stock("ITEM-B", "004", 10)
        };

        var result = Engine.Allocate(zone, Array.Empty<ZoneWarehouse>(), new[] { lA, lB }, stocks);

        Assert.Equal(2, result.AllocationTier);
        Assert.Equal(2, result.BaseResult.Fragments.Count);
        // {001,002} must win over {003,004}
        Assert.DoesNotContain(result.BaseResult.Fragments, f => f.WhsCode == "003");
        Assert.DoesNotContain(result.BaseResult.Fragments, f => f.WhsCode == "004");
    }

    // ── T20 ──────────────────────────────────────────────────────────────────
    // Shortage (Tier 5): HasShortage=true; cannot be used for SAP mutation.
    // The engine result itself signals this: BaseResult.HasShortage = true.
    [Fact]
    public void T20_Tier5_Shortage_ResultSignalsNoSapMutation()
    {
        var zone  = new[] { W("001", 1), W("002", 2) };
        var line  = Line("ITEM-A", 20);
        var stocks = new[]
        {
            Stock("ITEM-A", "001", 5),
            Stock("ITEM-A", "002", 5)
        };

        var result = Engine.Allocate(zone, Array.Empty<ZoneWarehouse>(), new[] { line }, stocks);

        // HasShortage=true is the guard against SAP mutation at the orchestration level.
        Assert.True(result.BaseResult.HasShortage);
        Assert.Equal(5, result.AllocationTier);
        // AllocatedQty + UnallocatedQty == RequestedQty
        decimal totalAllocated    = result.BaseResult.Fragments.Sum(f => f.AllocatedQty);
        decimal totalUnallocated  = result.BaseResult.Fragments.Sum(f => f.UnallocatedQty);
        Assert.Equal(20m, totalAllocated + totalUnallocated);
    }
}
