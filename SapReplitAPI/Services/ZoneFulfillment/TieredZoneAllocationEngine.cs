using SapReplitAPI.Models.ZoneFulfillment;

namespace SapReplitAPI.Services.ZoneFulfillment;

/// <summary>
/// Tiered Zone Allocation Engine — implements the Gate 1 approved Tier 1–5 hierarchy.
///
/// The zone parameter must already be in origin-aware priority order (built by
/// TieredWarehousePriorityResolver before this engine is called).
/// This engine is SEPARATE from ZoneAllocationEngine (Legacy) and does not modify it.
///
/// Tier hierarchy (best → worst):
///   Tier 1: Whole basket from one warehouse. First qualifying WHS in priority order wins.
///   Tier 2: Minimum warehouse set, whole lines only, no splits.
///           Equal-cardinality sets compared by lexicographic priority-index vector.
///   Tier 3: Whole-line fallback. First WHS in priority order that can supply the full
///           requested line qty without splitting.
///   Tier 4: Line split as last resort. Cascade in priority order.
///   Tier 5: Shortage (HasShortage=true). HTTP 422. No SAP mutation.
///
/// AllocationTier = highest (worst) SourceTier actually used across all fragments.
/// This engine is pure — no I/O, no DI side-effects. Fully testable in isolation.
/// </summary>
public sealed class TieredZoneAllocationEngine
{
    public TieredAllocationResult Allocate(
        IReadOnlyList<ZoneWarehouse>     zone,
        IReadOnlyList<DomainRequestLine> lines,
        IReadOnlyList<OitwSnapshot>      snapshots)
    {
        if (zone.Count == 0)
            throw new InvalidOperationException("Zone must contain at least one warehouse.");

        var available    = BuildAvailableMap(snapshots);
        var orderedLines = lines.OrderBy(l => l.LineSeq).ToList();
        var demandByItem = orderedLines
            .GroupBy(l => l.ItemCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Sum(l => l.RequestedQty), StringComparer.OrdinalIgnoreCase);

        // ── Tier 1: whole basket from single WHS ──────────────────────────────
        foreach (var whs in zone)
        {
            bool canAll = demandByItem.All(kvp =>
                available.GetValueOrDefault((kvp.Key, whs.WhsCode)) >= kvp.Value);
            if (!canAll) continue;

            var frags = orderedLines
                .Select(l => new AllocationFragment(l.RequestLineId, whs.WhsCode, l.RequestedQty, 0m))
                .ToList();
            var fragTiers = frags
                .Select(f => (f.RequestLineId, f.WhsCode, SourceTier: 1))
                .ToList<(Guid, string, int)>();

            return new TieredAllocationResult
            {
                BaseResult       = new AllocationResult { Fragments = frags, HasShortage = false },
                AllocationTier   = 1,
                AllocationReason = "Tier1: single warehouse supplies full basket",
                FragmentTiers    = fragTiers
            };
        }

        // ── Tier 2: minimum warehouse set, whole lines ────────────────────────
        var tier2 = TryTier2(zone, orderedLines, available);
        if (tier2 is not null) return tier2;

        // ── Tier 3 / 4 / 5: line-by-line fallback ────────────────────────────
        return AllocateLineByLine(zone, orderedLines, available);
    }

    // ── Tier 2 ────────────────────────────────────────────────────────────────

    private TieredAllocationResult? TryTier2(
        IReadOnlyList<ZoneWarehouse>          zone,
        List<DomainRequestLine>               lines,
        Dictionary<(string, string), decimal> available)
    {
        var whsList  = zone.Select(w => w.WhsCode).ToList();
        var priIndex = zone.Select((w, i) => (w.WhsCode, i))
                          .ToDictionary(x => x.WhsCode, x => x.i);
        int n = whsList.Count;

        int         bestSize = int.MaxValue;
        List<string>? bestSet = null;

        // Exhaustive subset enumeration (n ≤ 4 → max 15 non-empty subsets).
        // Sort each subset by priority index for deterministic stock simulation.
        for (int mask = 1; mask < (1 << n); mask++)
        {
            var subset = new List<string>();
            for (int b = 0; b < n; b++)
                if ((mask & (1 << b)) != 0)
                    subset.Add(whsList[b]);

            if (subset.Count > bestSize) continue;

            // Sort by priority for deterministic simulation
            subset.Sort((a, b2) => priIndex[a].CompareTo(priIndex[b2]));

            if (!CanFulfillWholeLines(subset, lines, available)) continue;

            if (subset.Count < bestSize ||
                (subset.Count == bestSize && CompareVectors(subset, bestSet!, priIndex) < 0))
            {
                bestSize = subset.Count;
                bestSet  = new List<string>(subset); // already sorted
            }
        }

        if (bestSet is null) return null;

        // Assign whole lines within bestSet, consuming stock deterministically
        var stock     = new Dictionary<(string, string), decimal>(available);
        var frags     = new List<AllocationFragment>();
        var fragTiers = new List<(Guid, string, int)>();

        foreach (var line in lines)
        {
            string? chosen = null;
            foreach (var whs in bestSet)
            {
                if (stock.GetValueOrDefault((line.ItemCode, whs)) >= line.RequestedQty)
                { chosen = whs; break; }
            }
            if (chosen is null) return null; // safety: should not happen

            frags.Add(new AllocationFragment(line.RequestLineId, chosen, line.RequestedQty, 0m));
            fragTiers.Add((line.RequestLineId, chosen, 2));
            stock[(line.ItemCode, chosen)] = stock.GetValueOrDefault((line.ItemCode, chosen)) - line.RequestedQty;
        }

        return new TieredAllocationResult
        {
            BaseResult       = new AllocationResult { Fragments = frags, HasShortage = false },
            AllocationTier   = 2,
            AllocationReason = $"Tier2: minimum {bestSize} warehouse(s) supply all lines whole",
            FragmentTiers    = fragTiers
        };
    }

    private bool CanFulfillWholeLines(
        List<string>                          subset,
        List<DomainRequestLine>               lines,
        Dictionary<(string, string), decimal> available)
    {
        // Simulate stock consumption for each line — can every line be placed whole?
        var stock = new Dictionary<(string, string), decimal>(available);
        foreach (var line in lines)
        {
            bool placed = false;
            foreach (var whs in subset)
            {
                decimal avail = stock.GetValueOrDefault((line.ItemCode, whs));
                if (avail >= line.RequestedQty)
                {
                    stock[(line.ItemCode, whs)] = avail - line.RequestedQty;
                    placed = true;
                    break;
                }
            }
            if (!placed) return false;
        }
        return true;
    }

    private static int CompareVectors(
        List<string> a, List<string> b, Dictionary<string, int> priIndex)
    {
        // Both must be sorted by priority index before calling
        var va = a.Select(w => priIndex[w]).ToList();
        var vb = b.Select(w => priIndex[w]).ToList();
        for (int i = 0; i < Math.Min(va.Count, vb.Count); i++)
        {
            int cmp = va[i].CompareTo(vb[i]);
            if (cmp != 0) return cmp;
        }
        return va.Count.CompareTo(vb.Count);
    }

    // ── Tier 3 / 4 / 5: line-by-line ─────────────────────────────────────────

    private TieredAllocationResult AllocateLineByLine(
        IReadOnlyList<ZoneWarehouse>          zone,
        List<DomainRequestLine>               lines,
        Dictionary<(string, string), decimal> available)
    {
        var stock     = new Dictionary<(string, string), decimal>(available);
        var frags     = new List<AllocationFragment>();
        var fragTiers = new List<(Guid, string, int)>();
        int worstTier = 3;
        bool hasShortage = false;

        foreach (var line in lines)
        {
            // Tier 3: single WHS can supply the full line qty
            string? tier3Whs = null;
            foreach (var whs in zone)
            {
                if (stock.GetValueOrDefault((line.ItemCode, whs.WhsCode)) >= line.RequestedQty)
                { tier3Whs = whs.WhsCode; break; }
            }

            if (tier3Whs is not null)
            {
                frags.Add(new AllocationFragment(line.RequestLineId, tier3Whs, line.RequestedQty, 0m));
                fragTiers.Add((line.RequestLineId, tier3Whs, 3));
                stock[(line.ItemCode, tier3Whs)] =
                    stock.GetValueOrDefault((line.ItemCode, tier3Whs)) - line.RequestedQty;
                continue;
            }

            // Tier 4: split line across warehouses in priority order
            worstTier = Math.Max(worstTier, 4);
            decimal remaining = line.RequestedQty;
            string? primaryWhs = null;

            foreach (var whs in zone)
            {
                if (remaining <= 0m) break;
                decimal avail = stock.GetValueOrDefault((line.ItemCode, whs.WhsCode));
                if (avail <= 0m) continue;
                decimal take = Math.Min(avail, remaining);
                frags.Add(new AllocationFragment(line.RequestLineId, whs.WhsCode, take, 0m));
                fragTiers.Add((line.RequestLineId, whs.WhsCode, 4));
                stock[(line.ItemCode, whs.WhsCode)] = avail - take;
                primaryWhs ??= whs.WhsCode;
                remaining -= take;
            }

            if (remaining > 0m)
            {
                // Tier 5 shortage for this line
                hasShortage = true;
                worstTier   = 5;
                string shortageWhs = primaryWhs ?? zone[0].WhsCode;
                int existingIdx = frags.FindLastIndex(
                    f => f.RequestLineId == line.RequestLineId && f.WhsCode == shortageWhs);
                if (existingIdx >= 0)
                {
                    var ef = frags[existingIdx];
                    frags[existingIdx] = new AllocationFragment(
                        ef.RequestLineId, ef.WhsCode, ef.AllocatedQty, remaining);
                }
                else
                {
                    frags.Add(new AllocationFragment(line.RequestLineId, shortageWhs, 0m, remaining));
                    fragTiers.Add((line.RequestLineId, shortageWhs, 5));
                }
            }
        }

        string reason = worstTier switch
        {
            5 => "Tier5: shortage — insufficient combined stock across all warehouses",
            4 => "Tier4: line split required — no single warehouse has full line qty",
            _ => "Tier3: whole-line fallback — different warehouse per line"
        };

        return new TieredAllocationResult
        {
            BaseResult       = new AllocationResult { Fragments = frags, HasShortage = hasShortage },
            AllocationTier   = worstTier,
            AllocationReason = reason,
            FragmentTiers    = fragTiers
        };
    }

    // ── Helper ────────────────────────────────────────────────────────────────

    private static Dictionary<(string, string), decimal> BuildAvailableMap(
        IReadOnlyList<OitwSnapshot> snapshots)
        => snapshots.ToDictionary(s => (s.ItemCode, s.WhsCode), s => s.Available);
}
