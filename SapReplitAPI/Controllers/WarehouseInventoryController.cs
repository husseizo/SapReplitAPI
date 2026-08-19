using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SapReplitAPI.Models;
using SapReplitAPI.Services;
using SapReplitAPI.Services.Inventory;

namespace SapReplitAPI.Controllers;

[ApiController]
[Route("api/warehouse-inventory")]
[ServiceFilter(typeof(SapReplitAPI.Filters.ApiKeyAuthFilter))]
public class WarehouseInventoryController : ControllerBase
{
    private readonly WarehouseInventorySyncService _sync;
    private readonly CacheDbContext _db;
    private readonly SapService _sap;
    private readonly ILogger<WarehouseInventoryController> _log;

    public WarehouseInventoryController(
        WarehouseInventorySyncService sync,
        CacheDbContext db,
        SapService sap,
        ILogger<WarehouseInventoryController> log)
    {
        _sync = sync;
        _db   = db;
        _sap  = sap;
        _log  = log;
    }

    /// <summary>
    /// Trigger a full SAP → SQLite warehouse inventory snapshot (all 4 warehouses).
    /// Replaces the entire cache; removes stale rows.
    /// Intended for manual verification and the nightly Quartz job (Step B3).
    /// </summary>
    [HttpPost("sync/full")]
    public async Task<IActionResult> FullSync()
    {
        _log.LogInformation("[WHInv] Manual full sync triggered via API.");
        var (upserted, removed) = await _sync.FullSyncAsync();

        var meta = await _db.SyncMetadata
            .FirstOrDefaultAsync(m => m.Type == "WarehouseInventory.Source");

        return Ok(new
        {
            Upserted        = upserted,
            Removed         = removed,
            WatermarkAfter  = meta?.LastSyncedAt.ToString("yyyy-MM-dd HH:mm:ss UTC"),
            TotalCachedRows = await _db.WarehouseInventories.CountAsync()
        });
    }

    /// <summary>
    /// Trigger a delta sync — detects changed ItemCodes from OINM + ORDR + OITM
    /// since (last watermark − 2h) and re-fetches only affected rows.
    /// Safe to call repeatedly — idempotent within the same watermark window.
    /// </summary>
    [HttpPost("sync/delta")]
    public async Task<IActionResult> DeltaSync()
    {
        _log.LogInformation("[WHInv] Manual delta sync triggered via API.");

        // Capture value before the sync — EF tracking mutates the entity in place,
        // so reading the entity reference *after* DeltaSyncAsync would show the new value.
        var watermarkBefore = (await _db.SyncMetadata.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Type == "WarehouseInventory.Source"))
            ?.LastSyncedAt.ToString("yyyy-MM-dd HH:mm:ss UTC") ?? "(none)";

        var (upserted, removed) = await _sync.DeltaSyncAsync();

        var watermarkAfter = (await _db.SyncMetadata.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Type == "WarehouseInventory.Source"))
            ?.LastSyncedAt.ToString("yyyy-MM-dd HH:mm:ss UTC");

        return Ok(new
        {
            WatermarkBefore = watermarkBefore,
            WatermarkAfter  = watermarkAfter,
            Upserted        = upserted,
            Removed         = removed,
            TotalCachedRows = await _db.WarehouseInventories.CountAsync()
        });
    }

    /// <summary>
    /// Returns current SyncMetadata watermark and total cached row count.
    /// Use to verify the cache is being updated correctly.
    /// </summary>
    [HttpGet("status")]
    public async Task<IActionResult> Status()
    {
        var meta = await _db.SyncMetadata
            .FirstOrDefaultAsync(m => m.Type == "WarehouseInventory.Source");

        var byWarehouse = await _db.WarehouseInventories
            .GroupBy(w => w.WhsCode)
            .Select(g => new { WhsCode = g.Key, Count = g.Count() })
            .ToListAsync();

        return Ok(new
        {
            Watermark       = meta?.LastSyncedAt.ToString("yyyy-MM-dd HH:mm:ss UTC") ?? "(not set)",
            TotalCachedRows = byWarehouse.Sum(w => w.Count),
            ByWarehouse     = byWarehouse.OrderBy(w => w.WhsCode)
        });
    }

    /// <summary>
    /// Spot-check: compares cached warehouse rows for a single ItemCode against live SAP.
    /// Verifies: OnHand, IsCommitted, AvailableToSell = OnHand - IsCommitted.
    /// Requires SAP to be connected. Use an item with open SOs to verify IsCommitted.
    /// </summary>
    [HttpGet("spot-check/{itemCode}")]
    public async Task<IActionResult> SpotCheck(string itemCode)
    {
        // Materialize first — SQLite EF provider can't handle decimal arithmetic in SQL projection
        var rawCached = await _db.WarehouseInventories
            .Where(w => w.ItemCode == itemCode)
            .OrderBy(w => w.WhsCode)
            .ToListAsync();

        var cached = rawCached.Select(w => new
        {
            w.WhsCode,
            w.WarehouseName,
            w.OnHand,
            w.IsCommitted,
            w.AvailableToSell,
            ComputedAvail = w.OnHand - w.IsCommitted,
            AvailMatches  = (w.OnHand - w.IsCommitted) == w.AvailableToSell,
            w.IsBinManaged,
            w.LastUpdated
        }).ToList();

        if (cached.Count == 0)
            return NotFound(new { Message = $"ItemCode '{itemCode}' not found in WarehouseInventory cache." });

        // Re-query SAP live for the same item
        var live = _sap.GetWarehouseInventorySnapshotForItems(new[] { itemCode });

        var comparison = cached.Select(c =>
        {
            var sapRow = live.FirstOrDefault(l => l.WhsCode == c.WhsCode);
            return new
            {
                WhsCode       = c.WhsCode,
                Cached = new
                {
                    c.OnHand,
                    c.IsCommitted,
                    c.AvailableToSell,
                    c.AvailMatches,
                    c.LastUpdated
                },
                SapLive = sapRow is null ? null : (object)new
                {
                    sapRow.OnHand,
                    sapRow.IsCommitted,
                    sapRow.AvailableToSell,
                    ComputedAvail = sapRow.OnHand - sapRow.IsCommitted
                },
                OnHandMatch     = sapRow is not null && sapRow.OnHand          == c.OnHand,
                CommittedMatch  = sapRow is not null && sapRow.IsCommitted     == c.IsCommitted,
                AvailMatch      = sapRow is not null && sapRow.AvailableToSell == c.AvailableToSell
            };
        }).ToList();

        return Ok(new
        {
            ItemCode   = itemCode,
            CachedRows = cached.Count,
            LiveRows   = live.Count,
            Comparison = comparison,
            AllOnHandMatch    = comparison.All(r => r.OnHandMatch),
            AllCommittedMatch = comparison.All(r => r.CommittedMatch),
            AllAvailMatch     = comparison.All(r => r.AvailMatch)
        });
    }

    /// <summary>
    /// Returns up to 10 items with non-zero IsCommitted (have open SOs) for spot-check selection.
    /// </summary>
    [HttpGet("committed-sample")]
    public async Task<IActionResult> CommittedSample()
    {
        // Materialize before sort — SQLite EF provider can't sort decimal columns in SQL
        var all = await _db.WarehouseInventories
            .Where(w => w.IsCommitted > 0)
            .Select(w => new { w.ItemCode, w.WhsCode, w.OnHand, w.IsCommitted, w.AvailableToSell })
            .ToListAsync();

        var sample = all.OrderByDescending(w => w.IsCommitted).Take(10);

        return Ok(sample);
    }
}
