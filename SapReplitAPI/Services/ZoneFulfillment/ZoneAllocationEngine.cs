using SapReplitAPI.Models.ZoneFulfillment;

namespace SapReplitAPI.Services.ZoneFulfillment;

/// <summary>
/// Pure allocation logic — no I/O, no COM, no DI side-effects.
/// Fully testable without SAP or database access.
///
/// Algorithm (2 phases, per the MOLAS_Warehouse_Priority_Automation_v2.0 PDF contract):
///
///   Phase 1 — Whole-order single-WHS (authoritative business rule):
///             Iterate zone warehouses in priority order. The FIRST warehouse
///             that can satisfy every item's FULL demand wins.
///             ALL order lines are allocated to that one warehouse — no split.
///             WhsCode may differ from the zone's "primary" warehouse;
///             e.g. U_DeliveryLocation=Mikocheni-side + WhsCode=002 is valid.
///
///   Phase 2 — Strict zone-priority cascade:
///             No single warehouse can satisfy the whole basket.
///             Cascade through warehouses in zone priority order, per item.
///             Each warehouse contributes max available to remaining demand.
///             Shortage (UnallocatedQty > 0) if any item's demand is still
///             unsatisfied after all warehouses are exhausted.
///
/// NOTE — Step 2 "minimum feasible subset" of the previous algorithm has been
/// REMOVED. The PDF contract requires strict priority-order cascading, not
/// subset optimisation. Comparison (selected cases):
///
///   Case A: 003=0, 002=2, 001=5, need=5, Mikocheni-side
///     Min-subset → [001:5] (skips 002, uses single WHS with best pri-sum)
///     PDF cascade → Phase 1 finds 001 can supply 5 → [001:5]     SAME
///
///   Case B: 003=3, 002=4, need=6
///     Min-subset → [003:3, 002:3]
///     PDF cascade → 003→3, 002→3                                  SAME
///
///   Case C: 003=0, 002=0, 001=3, 004=5, need=5
///     Min-subset → [004:5] (1-WHS optimal)
///     PDF cascade → Phase 1: 001=3<5, 004=5≥5 → [004:5]          SAME
///
///   Case D: 003=0, 002=1, 001=3, 004=5, need=5, MULTI-ITEM (Phase 1 fails)
///     Min-subset → [001:3, 004:2] or [004:5] depending on best subset
///     PDF cascade → [002:1, 001:3, 004:1]  — takes from each in order
///     DIFFERS when min-subset would skip an earlier WHS. Cascade wins per PDF.
///
/// Input assumption: OitwSnapshot.Available is already max(0, OnHand - IsCommited).
/// </summary>
public sealed class ZoneAllocationEngine
{
    /// <summary>
    /// Main entry point. Zone must be provided in priority order (Priority ASC).
    /// Lines are processed in LineSeq order for deterministic FIFO.
    /// </summary>
    public AllocationResult Allocate(
        IReadOnlyList<ZoneWarehouse>     zone,
        IReadOnlyList<DomainRequestLine> lines,
        IReadOnlyList<OitwSnapshot>      snapshots)
    {
        if (zone.Count == 0)
            throw new InvalidOperationException("Zone must contain at least one warehouse.");

        // Build available stock lookup: (ItemCode, WhsCode) → Available
        var available = snapshots.ToDictionary(
            s => (s.ItemCode, s.WhsCode),
            s => s.Available);

        // Aggregate demand per ItemCode (multiple lines may request same item)
        var demandByItem = lines
            .GroupBy(l => l.ItemCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Sum(l => l.RequestedQty), StringComparer.OrdinalIgnoreCase);

        var orderedLines = lines.OrderBy(l => l.LineSeq).ToList();

        // ── PHASE 1: Whole-order single-WHS test ──────────────────────────────
        // Iterate zone in priority order. First WHS that can supply every item wins.
        foreach (var whs in zone)
        {
            bool canSupplyAll = demandByItem.All(kvp =>
                available.GetValueOrDefault((kvp.Key, whs.WhsCode)) >= kvp.Value);

            if (canSupplyAll)
            {
                // Every request line goes to this warehouse — no split.
                var frags = orderedLines
                    .Select(l => new AllocationFragment(l.RequestLineId, whs.WhsCode, l.RequestedQty, 0m))
                    .ToList();
                return new AllocationResult { Fragments = frags, HasShortage = false };
            }
        }

        // ── PHASE 2: Strict zone-priority cascade ─────────────────────────────
        // No single warehouse qualifies. Cascade per item in zone priority order.
        // Deplete per-(ItemCode, WhsCode) so same-item multi-line orders stay coherent.

        var remaining = new Dictionary<(string, string), decimal>(available);
        var itemCascade = new Dictionary<string, List<(string WhsCode, decimal Allocated, decimal Unallocated)>>(
            StringComparer.OrdinalIgnoreCase);

        var processedItems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in orderedLines)
        {
            if (!processedItems.Add(line.ItemCode))
                continue; // already cascaded this item

            var whsStock = zone.ToDictionary(
                w => w.WhsCode,
                w => remaining.GetValueOrDefault((line.ItemCode, w.WhsCode), 0m));

            var cascadeResult = CascadeItem(demandByItem[line.ItemCode], zone, whsStock);
            itemCascade[line.ItemCode] = cascadeResult;

            // Deplete stock so subsequent items see committed quantities
            foreach (var (whs, alloc, _) in cascadeResult)
            {
                var key = (line.ItemCode, whs);
                if (!remaining.ContainsKey(key)) remaining[key] = 0m;
                remaining[key] = Math.Max(0m, remaining[key] - alloc);
            }
        }

        // Distribute item-level cascade results back to individual RequestLine fragments (FIFO)
        var resultFragments = new List<AllocationFragment>();
        var distributed     = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in orderedLines)
        {
            if (!distributed.Add(line.ItemCode))
                continue;

            var itemLines = orderedLines
                .Where(l => l.ItemCode.Equals(line.ItemCode, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var lineFrags = DistributeToLines(itemLines, itemCascade[line.ItemCode]);
            resultFragments.AddRange(lineFrags);
        }

        bool hasShortage = resultFragments.Any(f => f.HasShortage);
        return new AllocationResult { Fragments = resultFragments, HasShortage = hasShortage };
    }

    // ── Strict priority cascade for one item ──────────────────────────────────

    private static List<(string WhsCode, decimal Allocated, decimal Unallocated)> CascadeItem(
        decimal                      totalDemand,
        IReadOnlyList<ZoneWarehouse> zone,
        Dictionary<string, decimal>  whsStock)
    {
        var result     = new List<(string, decimal, decimal)>();
        decimal remain = totalDemand;
        string? primaryWhs = null;

        // Strict priority cascade: take from each warehouse in order.
        foreach (var whs in zone)
        {
            if (remain <= 0m) break;
            decimal take = Math.Min(whsStock.GetValueOrDefault(whs.WhsCode), remain);
            if (take > 0m)
            {
                result.Add((whs.WhsCode, take, 0m));
                primaryWhs ??= whs.WhsCode;
                remain -= take;
            }
        }

        // Shortage: attach to primary fragment (first WHS with any allocation).
        if (remain > 0m)
        {
            primaryWhs ??= zone[0].WhsCode; // zero-stock case: use highest-priority WHS
            int idx = result.FindIndex(r => r.Item1 == primaryWhs);
            if (idx >= 0)
            {
                var (whs2, alloc, _) = result[idx];
                result[idx] = (whs2, alloc, remain);
            }
            else
            {
                // Nothing allocated anywhere — single shortage fragment
                result.Insert(0, (primaryWhs, 0m, remain));
            }
        }

        return result;
    }

    // ── FIFO distribution from item-level to RequestLine fragments ─────────────

    private static List<AllocationFragment> DistributeToLines(
        IReadOnlyList<DomainRequestLine> lines,
        IReadOnlyList<(string WhsCode, decimal Allocated, decimal Unallocated)> itemAlloc)
    {
        var allocPool   = itemAlloc.ToDictionary(x => x.WhsCode, x => x.Allocated);
        var unallocPool = itemAlloc.ToDictionary(x => x.WhsCode, x => x.Unallocated);
        var whsOrder    = itemAlloc.Select(x => x.WhsCode).ToList();

        var result = new List<AllocationFragment>();

        foreach (var line in lines)
        {
            decimal need = line.RequestedQty;
            var lineFrags = new List<(string WhsCode, decimal Alloc, decimal Unalloc)>();

            // Fill from allocated pool first
            foreach (var whs in whsOrder)
            {
                if (need <= 0m) break;
                decimal avail = allocPool.GetValueOrDefault(whs);
                if (avail <= 0m) continue;
                decimal take = Math.Min(avail, need);
                lineFrags.Add((whs, take, 0m));
                allocPool[whs] = avail - take;
                need -= take;
            }

            // Then from unallocated pool (shortage)
            if (need > 0m)
            {
                foreach (var whs in whsOrder)
                {
                    if (need <= 0m) break;
                    decimal avail = unallocPool.GetValueOrDefault(whs);
                    if (avail <= 0m) continue;
                    decimal take = Math.Min(avail, need);
                    int idx = lineFrags.FindIndex(f => f.WhsCode == whs);
                    if (idx >= 0)
                        lineFrags[idx] = (whs, lineFrags[idx].Alloc, lineFrags[idx].Unalloc + take);
                    else
                        lineFrags.Add((whs, 0m, take));
                    unallocPool[whs] = avail - take;
                    need -= take;
                }
            }

            foreach (var (whs, alloc, unalloc) in lineFrags)
                result.Add(new AllocationFragment(line.RequestLineId, whs, alloc, unalloc));
        }

        return result;
    }
}
