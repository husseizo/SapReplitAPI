using SapReplitAPI.Models.ZoneFulfillment;

namespace SapReplitAPI.Services.ZoneFulfillment;

/// <summary>
/// D1 Tiered Zone Allocation Engine — implements the Tier 1–5 hierarchy with
/// strict Home-zone-first semantics.
///
/// D1 invariant: Tier 1 and Tier 2 search HOME ONLY. Fallback warehouses are
/// never eligible for Tier 1 or Tier 2, even if they alone can supply the full basket.
///
/// Tier hierarchy (best → worst):
///   Tier 1: One HOME warehouse supplies the whole basket. First qualifying HOME WHS wins.
///   Tier 2: No single HOME WHS qualifies, but HOME warehouses collectively can place every
///           line whole. Minimum HOME warehouse subset; equal-cardinality sets tie-broken
///           by lexicographic priority-index vector.
///   Tier 3: Tier 2 failed (at least one line not coverable whole in HOME).
///           Home-coverable lines → minimum HOME set (preserving whole assignments).
///           Non-home-coverable lines → first FALLBACK WHS that can supply the whole line.
///           Home lines retain SourceTier from their home assignment (may be 2-equivalent).
///           Fallback lines: SourceTier = 3.
///   Tier 4: Line still unresolved after Tier 3 (no single WHS in HOME or FALLBACK has full qty).
///           Cascade: HOME first in priority order, then FALLBACK. SourceTier = 4.
///   Tier 5: Combined stock (home + fallback) insufficient. HasShortage = true. HTTP 422.
///
/// AllocationTier = highest (worst) SourceTier actually used across all fragments.
/// This engine is pure — no I/O, no DI side-effects. Fully testable in isolation.
/// </summary>
public sealed class TieredZoneAllocationEngine
{
    public TieredAllocationResult Allocate(
        IReadOnlyList<ZoneWarehouse>     home,
        IReadOnlyList<ZoneWarehouse>     fallback,
        IReadOnlyList<DomainRequestLine> lines,
        IReadOnlyList<OitwSnapshot>      snapshots)
    {
        if (home.Count == 0)
            throw new InvalidOperationException("HomeZone must contain at least one warehouse.");

        var available    = BuildAvailableMap(snapshots);
        var orderedLines = lines.OrderBy(l => l.LineSeq).ToList();
        var demandByItem = orderedLines
            .GroupBy(l => l.ItemCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Sum(l => l.RequestedQty), StringComparer.OrdinalIgnoreCase);

        // ── Tier 1: whole basket from single HOME WHS ─────────────────────────
        foreach (var whs in home)
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
                AllocationReason = "Tier1: single home-zone warehouse supplies full basket",
                FragmentTiers    = fragTiers
            };
        }

        // ── Tier 2: minimum HOME set, whole lines ─────────────────────────────
        var tier2 = TryTier2(home, orderedLines, available);
        if (tier2 is not null) return tier2;

        // ── Tier 3 / 4 / 5: home-first line-by-line ──────────────────────────
        return AllocateLineByLine(home, fallback, orderedLines, available);
    }

    // ── Tier 2 (HOME only) ────────────────────────────────────────────────────

    private TieredAllocationResult? TryTier2(
        IReadOnlyList<ZoneWarehouse>          home,
        List<DomainRequestLine>               lines,
        Dictionary<(string, string), decimal> available)
    {
        var whsList  = home.Select(w => w.WhsCode).ToList();
        var priIndex = home.Select((w, i) => (w.WhsCode, i))
                          .ToDictionary(x => x.WhsCode, x => x.i);
        int n = whsList.Count;

        int          bestSize = int.MaxValue;
        List<string>? bestSet = null;

        // Exhaustive subset enumeration (home n ≤ 4 → max 15 non-empty subsets).
        for (int mask = 1; mask < (1 << n); mask++)
        {
            var subset = new List<string>();
            for (int b = 0; b < n; b++)
                if ((mask & (1 << b)) != 0)
                    subset.Add(whsList[b]);

            if (subset.Count > bestSize) continue;

            subset.Sort((a, b2) => priIndex[a].CompareTo(priIndex[b2]));

            if (!CanFulfillWholeLines(subset, lines, available)) continue;

            if (subset.Count < bestSize ||
                (subset.Count == bestSize && CompareVectors(subset, bestSet!, priIndex) < 0))
            {
                bestSize = subset.Count;
                bestSet  = new List<string>(subset);
            }
        }

        if (bestSet is null) return null;

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
            if (chosen is null) return null; // safety

            frags.Add(new AllocationFragment(line.RequestLineId, chosen, line.RequestedQty, 0m));
            fragTiers.Add((line.RequestLineId, chosen, 2));
            stock[(line.ItemCode, chosen)] = stock.GetValueOrDefault((line.ItemCode, chosen)) - line.RequestedQty;
        }

        return new TieredAllocationResult
        {
            BaseResult       = new AllocationResult { Fragments = frags, HasShortage = false },
            AllocationTier   = 2,
            AllocationReason = $"Tier2: minimum home-zone warehouse set ({bestSize}) supplies all lines whole",
            FragmentTiers    = fragTiers
        };
    }

    // ── Tier 3 / 4 / 5: home-first line-by-line ──────────────────────────────

    private TieredAllocationResult AllocateLineByLine(
        IReadOnlyList<ZoneWarehouse>          home,
        IReadOnlyList<ZoneWarehouse>          fallback,
        List<DomainRequestLine>               lines,
        Dictionary<(string, string), decimal> available)
    {
        var stock     = new Dictionary<(string, string), decimal>(available);
        var frags     = new List<AllocationFragment>();
        var fragTiers = new List<(Guid, string, int)>();
        int worstTier = 3;
        bool hasShortage = false;

        // ── Phase A: classify home-coverable lines ────────────────────────────
        // A line is home-coverable if some HOME warehouse has >= requestedQty in current stock.
        var homeCovarableIds = new HashSet<Guid>(
            lines
                .Where(l => home.Any(w => available.GetValueOrDefault((l.ItemCode, w.WhsCode)) >= l.RequestedQty))
                .Select(l => l.RequestLineId));

        // ── Phase B: allocate home-coverable lines via minimum HOME set ───────
        // Use same minimum-set algorithm as Tier 2 but limited to home-coverable lines.
        // If Phase B can't find a minimum set (e.g. multiple lines compete for the same
        // item's stock), those lines fall through to Phase D (Tier 4 split).
        var homeLines = lines.Where(l => homeCovarableIds.Contains(l.RequestLineId)).ToList();
        int fragsBeforePhaseB = frags.Count;
        if (homeLines.Count > 0)
        {
            AllocateHomeSubset(home, homeLines, stock, frags, fragTiers);
        }
        var allocatedByPhaseB = new HashSet<Guid>(
            frags.Skip(fragsBeforePhaseB).Select(f => f.RequestLineId));
        var homeFallthrough = homeLines
            .Where(l => !allocatedByPhaseB.Contains(l.RequestLineId))
            .ToList();

        // ── Phase C: non-home-coverable lines → whole-line FALLBACK ──────────
        var nonHomeLines = lines.Where(l => !homeCovarableIds.Contains(l.RequestLineId)).ToList();
        var tier4Lines   = new List<DomainRequestLine>(homeFallthrough); // Phase B rejects go straight to Tier 4

        foreach (var line in nonHomeLines)
        {
            // Try FALLBACK whole
            string? fbWhs = null;
            foreach (var whs in fallback)
            {
                if (stock.GetValueOrDefault((line.ItemCode, whs.WhsCode)) >= line.RequestedQty)
                { fbWhs = whs.WhsCode; break; }
            }

            if (fbWhs is not null)
            {
                frags.Add(new AllocationFragment(line.RequestLineId, fbWhs, line.RequestedQty, 0m));
                fragTiers.Add((line.RequestLineId, fbWhs, 3));
                stock[(line.ItemCode, fbWhs)] =
                    stock.GetValueOrDefault((line.ItemCode, fbWhs)) - line.RequestedQty;
                worstTier = Math.Max(worstTier, 3);
            }
            else
            {
                // No single WHS (home or fallback) can supply this line whole → Tier 4
                tier4Lines.Add(line);
            }
        }

        // ── Phase D: Tier 4 — split remaining lines (HOME first, then FALLBACK) ──
        foreach (var line in tier4Lines)
        {
            worstTier = Math.Max(worstTier, 4);
            decimal remaining = line.RequestedQty;
            string? primaryWhs = null;

            // HOME cascade first
            foreach (var whs in home)
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

            // FALLBACK cascade
            foreach (var whs in fallback)
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
                string shortageWhs = primaryWhs ?? home[0].WhsCode;
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
            5 => "Tier5: shortage — insufficient combined stock across home and fallback",
            4 => "Tier4: line split required after home/fallback whole-line search",
            _ => "Tier3: home-zone lines preserved; whole-line fallback used for non-home-coverable lines"
        };

        return new TieredAllocationResult
        {
            BaseResult       = new AllocationResult { Fragments = frags, HasShortage = hasShortage },
            AllocationTier   = worstTier,
            AllocationReason = reason,
            FragmentTiers    = fragTiers
        };
    }

    // ── AllocateHomeSubset: minimum HOME set for home-coverable lines ─────────
    // Same algorithm as TryTier2, but scoped to a subset of lines.
    // The SourceTier of home lines in a Tier-3 context is 2-equivalent (whole-line HOME).
    // We label them SourceTier=2 to indicate they came from minimum-home-set logic;
    // the overall AllocationTier will be 3 because non-home lines forced fallback.

    private static void AllocateHomeSubset(
        IReadOnlyList<ZoneWarehouse>          home,
        List<DomainRequestLine>               homeLines,
        Dictionary<(string, string), decimal> stock,
        List<AllocationFragment>              frags,
        List<(Guid, string, int)>             fragTiers)
    {
        var whsList  = home.Select(w => w.WhsCode).ToList();
        var priIndex = home.Select((w, i) => (w.WhsCode, i))
                          .ToDictionary(x => x.WhsCode, x => x.i);
        int n = whsList.Count;

        int          bestSize = int.MaxValue;
        List<string>? bestSet = null;

        for (int mask = 1; mask < (1 << n); mask++)
        {
            var subset = new List<string>();
            for (int b = 0; b < n; b++)
                if ((mask & (1 << b)) != 0)
                    subset.Add(whsList[b]);

            if (subset.Count > bestSize) continue;
            subset.Sort((a, b2) => priIndex[a].CompareTo(priIndex[b2]));

            if (!CanFulfillWholeLines(subset, homeLines, stock)) continue;

            if (subset.Count < bestSize ||
                (subset.Count == bestSize && CompareVectors(subset, bestSet!, priIndex) < 0))
            {
                bestSize = subset.Count;
                bestSet  = new List<string>(subset);
            }
        }

        if (bestSet is null)
        {
            // Should not happen (home-coverable check passed), but fall through gracefully
            return;
        }

        foreach (var line in homeLines)
        {
            string? chosen = null;
            foreach (var whs in bestSet)
            {
                if (stock.GetValueOrDefault((line.ItemCode, whs)) >= line.RequestedQty)
                { chosen = whs; break; }
            }
            if (chosen is null) continue; // safety

            frags.Add(new AllocationFragment(line.RequestLineId, chosen, line.RequestedQty, 0m));
            fragTiers.Add((line.RequestLineId, chosen, 2));
            stock[(line.ItemCode, chosen)] = stock.GetValueOrDefault((line.ItemCode, chosen)) - line.RequestedQty;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool CanFulfillWholeLines(
        List<string>                          subset,
        List<DomainRequestLine>               lines,
        Dictionary<(string, string), decimal> available)
    {
        var sim = new Dictionary<(string, string), decimal>(available);
        foreach (var line in lines)
        {
            bool placed = false;
            foreach (var whs in subset)
            {
                decimal avail = sim.GetValueOrDefault((line.ItemCode, whs));
                if (avail >= line.RequestedQty)
                {
                    sim[(line.ItemCode, whs)] = avail - line.RequestedQty;
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
        var va = a.Select(w => priIndex[w]).ToList();
        var vb = b.Select(w => priIndex[w]).ToList();
        for (int i = 0; i < Math.Min(va.Count, vb.Count); i++)
        {
            int cmp = va[i].CompareTo(vb[i]);
            if (cmp != 0) return cmp;
        }
        return va.Count.CompareTo(vb.Count);
    }

    private static Dictionary<(string, string), decimal> BuildAvailableMap(
        IReadOnlyList<OitwSnapshot> snapshots)
        => snapshots.ToDictionary(s => (s.ItemCode, s.WhsCode), s => s.Available);
}
