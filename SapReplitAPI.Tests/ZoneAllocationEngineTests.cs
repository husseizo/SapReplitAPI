using SapReplitAPI.Models.ZoneFulfillment;
using SapReplitAPI.Services.ZoneFulfillment;
using Xunit;

namespace SapReplitAPI.Tests;

/// <summary>
/// C-ALLOC-01 through C-ALLOC-15: pure allocation logic tests.
/// No COM, no database, no I/O.
/// Zone: 003(P1), 002(P2), 001(P3), 004(P4) — Mikocheni-side priority order.
/// </summary>
public sealed class ZoneAllocationEngineTests
{
    private static readonly ZoneAllocationEngine Engine = new();

    // Standard Mikocheni-side zone
    private static IReadOnlyList<ZoneWarehouse> MikZone =>
    [
        new("003", 1),
        new("002", 2),
        new("001", 3),
        new("004", 4)
    ];

    private static DomainRequestLine Line(Guid id, int seq, string item, decimal qty) =>
        new(id, seq, item, qty, 0m, null, null, null);

    private static OitwSnapshot Stock(string item, string whs, decimal avail) =>
        new(item, whs, avail);

    // ── C-ALLOC-01: Single WHS satisfies all demand (Step 1 path) ─────────────

    [Fact]
    public void C_ALLOC_01_SingleWhs_Satisfies_All_Demand()
    {
        var lineId = Guid.NewGuid();
        var lines = new[] { Line(lineId, 1, "ITEM-A", 5m) };
        var snap  = new[]
        {
            Stock("ITEM-A", "003", 10m),
            Stock("ITEM-A", "002",  3m)
        };

        var result = Engine.Allocate(MikZone, lines, snap);

        Assert.False(result.HasShortage);
        var frags = result.Fragments.Where(f => f.RequestLineId == lineId).ToList();
        // Step 1: 003 can satisfy all 5, so only one fragment
        Assert.Single(frags);
        Assert.Equal("003", frags[0].WhsCode);
        Assert.Equal(5m, frags[0].AllocatedQty);
        Assert.Equal(0m, frags[0].UnallocatedQty);
        Assert.Equal(5m, frags[0].SoLineQty);
    }

    // ── C-ALLOC-02: Step 1 picks highest-priority WHS when multiple can satisfy ─

    [Fact]
    public void C_ALLOC_02_Step1_Picks_HighestPriority_WHS()
    {
        var lineId = Guid.NewGuid();
        var lines = new[] { Line(lineId, 1, "ITEM-B", 4m) };
        var snap  = new[]
        {
            Stock("ITEM-B", "003", 5m),  // P1 — can satisfy
            Stock("ITEM-B", "002", 5m)   // P2 — can also satisfy
        };

        var result = Engine.Allocate(MikZone, lines, snap);

        Assert.False(result.HasShortage);
        var frag = Assert.Single(result.Fragments);
        Assert.Equal("003", frag.WhsCode);  // highest priority wins
        Assert.Equal(4m, frag.AllocatedQty);
    }

    // ── C-ALLOC-03: Step 2 minimum feasible subset (2-WHS combo) ──────────────

    [Fact]
    public void C_ALLOC_03_Step2_MinimumFeasibleSubset()
    {
        var lineId = Guid.NewGuid();
        var lines = new[] { Line(lineId, 1, "ITEM-C", 8m) };
        var snap  = new[]
        {
            Stock("ITEM-C", "003", 5m),  // P1 alone: 5 < 8
            Stock("ITEM-C", "002", 5m),  // P2 alone: 5 < 8; 003+002 = 10 >= 8
            Stock("ITEM-C", "001", 5m),  // P3
            Stock("ITEM-C", "004", 5m)   // P4
        };

        var result = Engine.Allocate(MikZone, lines, snap);

        Assert.False(result.HasShortage);
        var frags = result.Fragments.ToList();
        // Minimum feasible subset is size 2; best is 003+002 (lowest priority sum = 1+2=3)
        Assert.Equal(2, frags.Count);
        var whsCodes = frags.Select(f => f.WhsCode).ToHashSet();
        Assert.Contains("003", whsCodes);
        Assert.Contains("002", whsCodes);
        Assert.Equal(8m, frags.Sum(f => f.AllocatedQty));
    }

    // ── C-ALLOC-04: Step 3 partial — shortage merged into primary fragment ─────

    [Fact]
    public void C_ALLOC_04_Step3_Partial_Shortage_Into_Primary()
    {
        var lineId = Guid.NewGuid();
        var lines = new[] { Line(lineId, 1, "ITEM-D", 20m) };
        var snap  = new[]
        {
            Stock("ITEM-D", "003", 6m),
            Stock("ITEM-D", "002", 4m),
            Stock("ITEM-D", "001", 3m),
            Stock("ITEM-D", "004", 2m)  // total = 15 < 20 → shortage = 5
        };

        var result = Engine.Allocate(MikZone, lines, snap);

        Assert.True(result.HasShortage);
        var frags = result.Fragments.ToList();
        Assert.Equal(20m, frags.Sum(f => f.SoLineQty)); // SoLineQty = Alloc + Unalloc = 20

        // Primary fragment is the first WHS with any allocated qty (003)
        var primary = frags.First(f => f.WhsCode == "003");
        Assert.Equal(5m, primary.UnallocatedQty); // shortage = 20 - 15 = 5
        Assert.Equal(6m, primary.AllocatedQty);
        Assert.Equal(11m, primary.SoLineQty);
    }

    // ── C-ALLOC-05: Zero stock everywhere — full shortage on first WHS ─────────

    [Fact]
    public void C_ALLOC_05_ZeroStock_Full_Shortage_On_FirstWhs()
    {
        var lineId = Guid.NewGuid();
        var lines = new[] { Line(lineId, 1, "ITEM-E", 10m) };
        var snap  = new[]
        {
            Stock("ITEM-E", "003", 0m),
            Stock("ITEM-E", "002", 0m),
            Stock("ITEM-E", "001", 0m),
            Stock("ITEM-E", "004", 0m)
        };

        var result = Engine.Allocate(MikZone, lines, snap);

        Assert.True(result.HasShortage);
        // One fragment on the first WHS (003) bearing full shortage
        var frag = Assert.Single(result.Fragments);
        Assert.Equal("003", frag.WhsCode);
        Assert.Equal(0m,  frag.AllocatedQty);
        Assert.Equal(10m, frag.UnallocatedQty);
        Assert.Equal(10m, frag.SoLineQty);
    }

    // ── C-ALLOC-06: Two lines, same item — FIFO distribution ──────────────────

    [Fact]
    public void C_ALLOC_06_TwoLines_SameItem_FIFO_Distribution()
    {
        var line1 = Guid.NewGuid();
        var line2 = Guid.NewGuid();
        var lines = new[]
        {
            Line(line1, 1, "ITEM-F", 4m),  // seq 1 — filled first
            Line(line2, 2, "ITEM-F", 3m)   // seq 2
        };
        var snap = new[]
        {
            Stock("ITEM-F", "003", 5m),   // total demand = 7, 003 has 5
            Stock("ITEM-F", "002", 3m)    // 002 has 3 → total 8 >= 7
        };

        var result = Engine.Allocate(MikZone, lines, snap);

        Assert.False(result.HasShortage);
        // Line1 (seq=1) gets 4 first — from 003 (4 of 5)
        var l1frags = result.ForLine(line1).ToList();
        Assert.Single(l1frags);
        Assert.Equal("003", l1frags[0].WhsCode);
        Assert.Equal(4m, l1frags[0].AllocatedQty);

        // Line2 (seq=2) gets remaining 1 from 003 + 2 from 002
        var l2frags = result.ForLine(line2).OrderBy(f => f.WhsCode).ToList();
        Assert.Equal(3m, l2frags.Sum(f => f.AllocatedQty));
    }

    // ── C-ALLOC-07: Two different items — each allocated independently ─────────

    [Fact]
    public void C_ALLOC_07_TwoDifferentItems_Independent_Allocation()
    {
        var la = Guid.NewGuid();
        var lb = Guid.NewGuid();
        var lines = new[]
        {
            Line(la, 1, "ITEM-G", 5m),
            Line(lb, 2, "ITEM-H", 3m)
        };
        var snap = new[]
        {
            Stock("ITEM-G", "003", 6m),
            Stock("ITEM-H", "003", 2m),
            Stock("ITEM-H", "002", 2m)
        };

        var result = Engine.Allocate(MikZone, lines, snap);

        Assert.False(result.HasShortage);

        var gaFrag = result.ForLine(la).Single();
        Assert.Equal("003", gaFrag.WhsCode);
        Assert.Equal(5m, gaFrag.AllocatedQty);

        var hFrags = result.ForLine(lb).ToList();
        Assert.Equal(3m, hFrags.Sum(f => f.AllocatedQty));
    }

    // ── C-ALLOC-08: SoLineQty invariant — AllocatedQty + UnallocatedQty = RequestedQty ──

    [Fact]
    public void C_ALLOC_08_SoLineQty_Invariant_Holds_Always()
    {
        var line1 = Guid.NewGuid();
        var line2 = Guid.NewGuid();
        var lines = new[]
        {
            Line(line1, 1, "ITEM-I", 10m),
            Line(line2, 2, "ITEM-I", 7m)
        };
        var snap = new[]
        {
            Stock("ITEM-I", "003", 4m),
            Stock("ITEM-I", "002", 5m),
            Stock("ITEM-I", "001", 2m)  // total = 11 < 17 → shortage
        };

        var result = Engine.Allocate(MikZone, lines, snap);

        // SoLineQty per line must equal RequestedQty
        var l1Total = result.ForLine(line1).Sum(f => f.SoLineQty);
        var l2Total = result.ForLine(line2).Sum(f => f.SoLineQty);
        Assert.Equal(10m, l1Total);
        Assert.Equal(7m,  l2Total);
    }

    // ── C-ALLOC-09: Cluster-side zone — different priority order ──────────────

    [Fact]
    public void C_ALLOC_09_ClusterSide_Zone_Different_Priority()
    {
        IReadOnlyList<ZoneWarehouse> clusterZone =
        [
            new("002", 1),
            new("001", 2),
            new("004", 3),
            new("003", 4)
        ];

        var lineId = Guid.NewGuid();
        var lines = new[] { Line(lineId, 1, "ITEM-J", 5m) };
        var snap = new[]
        {
            Stock("ITEM-J", "003", 10m),  // Cluster-side P4
            Stock("ITEM-J", "002", 8m)    // Cluster-side P1
        };

        var result = Engine.Allocate(clusterZone, lines, snap);

        Assert.False(result.HasShortage);
        var frag = Assert.Single(result.Fragments);
        Assert.Equal("002", frag.WhsCode);  // P1 for cluster-side
        Assert.Equal(5m, frag.AllocatedQty);
    }

    // ── C-ALLOC-10: Empty stock entries treated as 0 ──────────────────────────

    [Fact]
    public void C_ALLOC_10_Missing_Snapshot_Treated_As_Zero()
    {
        var lineId = Guid.NewGuid();
        var lines = new[] { Line(lineId, 1, "ITEM-K", 3m) };
        var snap  = new[]
        {
            // Only 002 has stock; 003, 001, 004 have no snapshot rows
            Stock("ITEM-K", "002", 5m)
        };

        var result = Engine.Allocate(MikZone, lines, snap);

        Assert.False(result.HasShortage);
        var frag = Assert.Single(result.Fragments);
        Assert.Equal("002", frag.WhsCode);
        Assert.Equal(3m, frag.AllocatedQty);
    }

    // ── C-ALLOC-11: Duplicate item demand aggregated before allocation ─────────

    [Fact]
    public void C_ALLOC_11_DuplicateItemDemand_Aggregated()
    {
        // Three lines for same item: total = 3+2+1 = 6
        var l1 = Guid.NewGuid();
        var l2 = Guid.NewGuid();
        var l3 = Guid.NewGuid();
        var lines = new[]
        {
            Line(l1, 1, "ITEM-L", 3m),
            Line(l2, 2, "ITEM-L", 2m),
            Line(l3, 3, "ITEM-L", 1m)
        };
        var snap = new[]
        {
            Stock("ITEM-L", "003", 4m),
            Stock("ITEM-L", "002", 4m)  // 003 alone: 4 < 6; 003+002=8 >= 6 → step 2
        };

        var result = Engine.Allocate(MikZone, lines, snap);

        Assert.False(result.HasShortage);
        // Total allocated must equal total demanded (6)
        Assert.Equal(6m, result.Fragments.Sum(f => f.AllocatedQty));
        // Each line's SoLineQty must match its RequestedQty
        Assert.Equal(3m, result.ForLine(l1).Sum(f => f.SoLineQty));
        Assert.Equal(2m, result.ForLine(l2).Sum(f => f.SoLineQty));
        Assert.Equal(1m, result.ForLine(l3).Sum(f => f.SoLineQty));
    }

    // ── C-ALLOC-12: Step 2 prefers subset with better aggregate priority ────────

    [Fact]
    public void C_ALLOC_12_Step2_Prefers_Better_AggPriority()
    {
        // demand = 6; 003(P1,qty=3) + 002(P2,qty=4) = 7 >= 6, priSum=3
        //           003(P1,qty=3) + 001(P3,qty=4) = 7 >= 6, priSum=4
        // Prefers 003+002 (priSum=3 < 4)
        var lineId = Guid.NewGuid();
        var lines = new[] { Line(lineId, 1, "ITEM-M", 6m) };
        var snap = new[]
        {
            Stock("ITEM-M", "003", 3m),
            Stock("ITEM-M", "002", 4m),
            Stock("ITEM-M", "001", 4m),
            Stock("ITEM-M", "004", 0m)
        };

        var result = Engine.Allocate(MikZone, lines, snap);

        Assert.False(result.HasShortage);
        var whsCodes = result.Fragments.Select(f => f.WhsCode).ToHashSet();
        Assert.Contains("003", whsCodes);
        Assert.Contains("002", whsCodes);
        Assert.DoesNotContain("001", whsCodes);
    }

    // ── C-ALLOC-13: Single item, exact match on one WHS ───────────────────────

    [Fact]
    public void C_ALLOC_13_ExactMatch_Single_Whs()
    {
        var lineId = Guid.NewGuid();
        var lines  = new[] { Line(lineId, 1, "ITEM-N", 7m) };
        var snap   = new[]
        {
            Stock("ITEM-N", "003", 7m),   // exact match
            Stock("ITEM-N", "002", 5m)
        };

        var result = Engine.Allocate(MikZone, lines, snap);

        Assert.False(result.HasShortage);
        var frag = Assert.Single(result.Fragments);
        Assert.Equal("003", frag.WhsCode);
        Assert.Equal(7m, frag.AllocatedQty);
    }

    // ── C-ALLOC-14: Partial shortage with multiple lines — FIFO fills higher-seq last ──

    [Fact]
    public void C_ALLOC_14_Partial_Shortage_FIFO_Higher_Seq_Bears_Shortage()
    {
        var l1 = Guid.NewGuid();
        var l2 = Guid.NewGuid();
        // Seq1 wants 5, Seq2 wants 5 — total 10; only 7 available
        var lines = new[]
        {
            Line(l1, 1, "ITEM-O", 5m),
            Line(l2, 2, "ITEM-O", 5m)
        };
        var snap = new[]
        {
            Stock("ITEM-O", "003", 4m),
            Stock("ITEM-O", "002", 3m)   // total 7
        };

        var result = Engine.Allocate(MikZone, lines, snap);

        Assert.True(result.HasShortage);
        // Line1 (seq=1) should get fully allocated (FIFO) — 5 units
        var l1Total = result.ForLine(l1).Sum(f => f.SoLineQty);
        Assert.Equal(5m, l1Total);

        // Line2 gets 2 allocated + 3 shortage
        var l2Alloc   = result.ForLine(l2).Sum(f => f.AllocatedQty);
        var l2Unalloc = result.ForLine(l2).Sum(f => f.UnallocatedQty);
        Assert.Equal(2m, l2Alloc);
        Assert.Equal(3m, l2Unalloc);
    }

    // ── C-ALLOC-15: Empty zone throws InvalidOperationException ───────────────

    [Fact]
    public void C_ALLOC_15_EmptyZone_Throws()
    {
        var lineId = Guid.NewGuid();
        var lines  = new[] { Line(lineId, 1, "ITEM-P", 1m) };

        Assert.Throws<InvalidOperationException>(() =>
            Engine.Allocate([], lines, []));
    }
}
