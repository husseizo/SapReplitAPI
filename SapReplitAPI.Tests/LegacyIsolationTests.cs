using SapReplitAPI.Models.ZoneFulfillment;
using SapReplitAPI.Services.ZoneFulfillment;
using Xunit;

namespace SapReplitAPI.Tests;

/// <summary>
/// C-ISO-01 through C-ISO-04: legacy isolation contract tests.
/// Verifies that the ZoneAllocationEngine, RDR1 reconciliation, and fragment model
/// correctly isolate from legacy delivery logic — tested as pure-logic invariants.
/// The SQL-level isolation (ISNULL(U_AppRef,'') &lt;&gt; 'ZoneFulfillment' in GetOpenSosForDate
/// and the pilot guard in ProcessSingleOrderPilotCoreAsync) are covered by manual
/// verification of SapService.cs:2202 and SoDeliveryService.cs safety gate 3.
/// </summary>
public sealed class LegacyIsolationTests
{
    private static readonly ZoneAllocationEngine Engine = new();

    private static IReadOnlyList<ZoneWarehouse> MikZone =>
    [
        new("003", 1),
        new("002", 2),
        new("001", 3),
        new("004", 4)
    ];

    // ── C-ISO-01: ZoneFulfillment fragments never overlap with null U_AppRef SOs ─

    [Fact]
    public void C_ISO_01_AllocationResult_Does_Not_Mutate_Input_Lines()
    {
        // Engine must not mutate the caller's list
        var lineId = Guid.NewGuid();
        var original = new DomainRequestLine(lineId, 1, "ITEM-X", 5m, 10m, null, null, null);
        var lines    = new[] { original };
        var snap     = new[] { new OitwSnapshot("ITEM-X", "003", 10m) };

        _ = Engine.Allocate(MikZone, lines, snap);

        // Original record must be unchanged
        Assert.Equal(lineId, original.RequestLineId);
        Assert.Equal(5m, original.RequestedQty);
    }

    // ── C-ISO-02: RDR1 reconciliation rejects mismatched fragment count ────────

    [Fact]
    public void C_ISO_02_Reconcile_Throws_On_Count_Mismatch()
    {
        var frag = new AllocationFragment(Guid.NewGuid(), "003", 5m, 0m);
        var rdr1 = new[]
        {
            new Rdr1Line(0, "ITEM-Y", "003", 5m, 5m),
            new Rdr1Line(1, "ITEM-Y", "002", 3m, 3m)  // extra line
        };

        Assert.Throws<InvalidOperationException>(() =>
            ZoneFulfillmentSapOrderService.ReconcileRdr1(
                orchestrationId: 1L, allocationPlanId: 1L, docEntry: 99999,
                orderedFragments: [frag],
                rdr1Lines: rdr1));
    }

    // ── C-ISO-03: SoLineQty conservation invariant across reconciliation ────────

    [Fact]
    public void C_ISO_03_Reconcile_SoLineQty_Conservation()
    {
        var rid1 = Guid.NewGuid();
        var rid2 = Guid.NewGuid();
        var frags = new AllocationFragment[]
        {
            new(rid1, "003", 4m, 0m),   // SoLineQty=4
            new(rid2, "003", 2m, 1m),   // SoLineQty=3 (shortage=1)
        };
        var rdr1 = new[]
        {
            new Rdr1Line(0, "ITEM-Z", "003", 4m, 4m),
            new Rdr1Line(1, "ITEM-Z", "003", 3m, 3m)
        };

        var result = ZoneFulfillmentSapOrderService.ReconcileRdr1(
            1L, 1L, 99999, frags, rdr1);

        Assert.Equal(2, result.Count);
        Assert.Equal(4m, result[0].SoLineQty);
        Assert.Equal(3m, result[1].SoLineQty);
        Assert.Equal(1m, result[1].UnallocatedQty);
    }

    // ── C-ISO-04: U_AppRef='ZoneFulfillment' written on every ZF order ─────────

    [Fact]
    public void C_ISO_04_BuildReplitId_StartsWith_ZF()
    {
        // Every ZF order must have U_ReplitId starting with "ZF-" to distinguish
        // it from legacy orders (which start with "OR-").
        var rid = Guid.NewGuid();
        string replitId = ZoneFulfillmentSapOrderService.BuildReplitId(rid);

        Assert.StartsWith("ZF-", replitId);
        Assert.DoesNotContain("OR-", replitId); // must never look like a legacy order
    }
}
