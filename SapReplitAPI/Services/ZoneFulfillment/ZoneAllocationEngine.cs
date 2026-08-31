using SapReplitAPI.Models.ZoneFulfillment;

namespace SapReplitAPI.Services.ZoneFulfillment;

/// <summary>
/// Pure allocation logic — no I/O, no COM, no DI side-effects.
/// Fully testable without SAP or database access.
///
/// Algorithm (3 steps, applied per item across all aggregate demand):
///   Step 1 — Single-WHS: if one warehouse can satisfy all demand for an item,
///             assign everything to the highest-priority WHS that can.
///   Step 2 — Minimum feasible subset (≤ 15 subsets for ≤ 4 WHS):
///             find the smallest set of warehouses that jointly satisfy demand.
///             Among equally-small subsets, prefer highest-aggregate priority.
///   Step 3 — Partial: no subset satisfies full demand. Allocate greedily in
///             priority order; merge shortage into the primary fragment
///             (first WHS with any allocated qty, else first active WHS).
///
/// Input assumption: OitwSnapshot.Available is already max(0, OnHand - IsCommited).
/// </summary>
public sealed class ZoneAllocationEngine
{
    /// <summary>
    /// Main entry point. Zones must be provided in priority order (Priority ASC).
    /// Lines are processed in LineSeq order for deterministic FIFO.
    /// </summary>
    public AllocationResult Allocate(
        IReadOnlyList<ZoneWarehouse>    zone,
        IReadOnlyList<DomainRequestLine> lines,
        IReadOnlyList<OitwSnapshot>     snapshots)
    {
        if (zone.Count == 0)
            throw new InvalidOperationException("Zone must contain at least one warehouse.");

        // Build available stock lookup: (ItemCode, WhsCode) → Available
        var available = snapshots.ToDictionary(
            s => (s.ItemCode, s.WhsCode),
            s => s.Available);

        // Aggregate demand per ItemCode (multiple lines may request same item)
        var demandByItem = lines
            .GroupBy(l => l.ItemCode)
            .ToDictionary(g => g.Key, g => g.Sum(l => l.RequestedQty));

        var fragments = new List<AllocationFragment>();

        // Sort lines by LineSeq for FIFO distribution of shortage
        var orderedLines = lines.OrderBy(l => l.LineSeq).ToList();

        // Per-item allocation (tracks remaining stock mutations across lines)
        // Clone available dict so we can deplete it per item in order
        var remaining = new Dictionary<(string, string), decimal>(available);

        // Process each item group in LineSeq order (first line of item encountered)
        var processedItems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in orderedLines)
        {
            if (!processedItems.Add(line.ItemCode))
                continue; // already processed this item — fragments already generated

            var totalDemand = demandByItem[line.ItemCode];
            var itemLines   = orderedLines.Where(l =>
                l.ItemCode.Equals(line.ItemCode, StringComparison.OrdinalIgnoreCase)).ToList();

            // Per-WHS stock for this item
            var whsStock = zone.ToDictionary(
                w => w.WhsCode,
                w => remaining.GetValueOrDefault((line.ItemCode, w.WhsCode), 0m));

            var itemFragments = AllocateItem(line.ItemCode, totalDemand, zone, whsStock);

            // Distribute item-level fragments back to individual RequestLine fragments
            // using FIFO: fill each line's demand from allocated qty pool, WHS by WHS
            var lineFragments = DistributeToLines(itemLines, itemFragments);
            fragments.AddRange(lineFragments);

            // Deplete remaining stock so subsequent items see committed stock
            foreach (var (whs, alloc, _) in itemFragments)
            {
                var key = (line.ItemCode, whs);
                if (!remaining.ContainsKey(key)) remaining[key] = 0m;
                remaining[key] = Math.Max(0m, remaining[key] - alloc);
            }
        }

        bool hasShortage = fragments.Any(f => f.HasShortage);
        return new AllocationResult { Fragments = fragments, HasShortage = hasShortage };
    }

    // ── Item-level allocation ──────────────────────────────────────────────────

    private static List<(string WhsCode, decimal Allocated, decimal Unallocated)> AllocateItem(
        string itemCode,
        decimal totalDemand,
        IReadOnlyList<ZoneWarehouse> zone,
        Dictionary<string, decimal>  whsStock)
    {
        // Step 1: single WHS satisfies all
        foreach (var whs in zone)
        {
            if (whsStock.GetValueOrDefault(whs.WhsCode) >= totalDemand)
                return [(whs.WhsCode, totalDemand, 0m)];
        }

        // Step 2: minimum feasible subset (max 2^4=16 subsets for 4 WHS, skip empty)
        var codes = zone.Select(w => w.WhsCode).ToList();
        List<(string WhsCode, decimal Allocated, decimal Unallocated)>? bestSubset = null;
        int bestSize = int.MaxValue;
        int bestPrioritySum = int.MaxValue;

        for (int mask = 1; mask < (1 << codes.Count); mask++)
        {
            decimal supply = 0m;
            int    size    = 0;
            int    priSum  = 0;
            for (int i = 0; i < codes.Count; i++)
            {
                if ((mask & (1 << i)) == 0) continue;
                supply += whsStock.GetValueOrDefault(codes[i]);
                size++;
                priSum += zone[i].Priority;
            }

            if (supply < totalDemand) continue;          // can't satisfy
            if (size > bestSize)      continue;          // larger subset
            if (size == bestSize && priSum >= bestPrioritySum) continue; // worse priority

            // Build allocation for this subset
            var candidate = new List<(string, decimal, decimal)>();
            decimal leftover = totalDemand;
            for (int i = 0; i < codes.Count; i++)
            {
                if ((mask & (1 << i)) == 0) continue;
                decimal take = Math.Min(whsStock.GetValueOrDefault(codes[i]), leftover);
                candidate.Add((codes[i], take, 0m));
                leftover -= take;
                if (leftover <= 0m) break;
            }

            bestSubset      = candidate;
            bestSize        = size;
            bestPrioritySum = priSum;
        }

        if (bestSubset is not null)
            return bestSubset;

        // Step 3: partial — allocate greedily, merge shortage into primary fragment
        var result      = new List<(string, decimal, decimal)>();
        decimal remain  = totalDemand;
        string? primaryWhs = null;

        foreach (var whs in zone)
        {
            decimal take = Math.Min(whsStock.GetValueOrDefault(whs.WhsCode), remain);
            if (take > 0m)
            {
                result.Add((whs.WhsCode, take, 0m));
                primaryWhs ??= whs.WhsCode;
                remain -= take;
            }
        }

        if (remain > 0m)
        {
            // Shortage: attach to primary WHS
            primaryWhs ??= zone[0].WhsCode;
            int idx = result.FindIndex(r => r.Item1 == primaryWhs);
            if (idx >= 0)
            {
                var (whs2, alloc, _) = result[idx];
                result[idx] = (whs2, alloc, remain);
            }
            else
            {
                // Zero allocated everywhere — primary takes the full shortage
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
        // Build mutable pool: (WhsCode → remaining allocated, remaining unallocated)
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
