using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SapReplitAPI.Models;
using SapReplitAPI.Services;
using SapReplitAPI.Services.Inventory;

namespace SapReplitAPI.Controllers;

[ApiController]
[Route("api/bin-inventory")]
[ServiceFilter(typeof(SapReplitAPI.Filters.ApiKeyAuthFilter))]
public class BinInventoryController : ControllerBase
{
    private readonly BinInventorySyncService _sync;
    private readonly CacheDbContext _db;
    private readonly SapService _sap;
    private readonly ILogger<BinInventoryController> _log;

    public BinInventoryController(
        BinInventorySyncService sync,
        CacheDbContext db,
        SapService sap,
        ILogger<BinInventoryController> log)
    {
        _sync = sync;
        _db   = db;
        _sap  = sap;
        _log  = log;
    }

    /// <summary>
    /// Trigger a full SAP → SQLite bin inventory snapshot (all 4 bin-managed warehouses).
    /// Fetches OIBQ JOIN OBIN JOIN OWHS, positive stock only. Replaces entire cache.
    /// Returns diagnostic report including reconciliation vs WarehouseInventory.OnHand.
    /// </summary>
    [HttpPost("sync/full")]
    public async Task<IActionResult> FullSync()
    {
        _log.LogInformation("[BinInv] Manual full sync triggered via API.");
        var report = await _sync.FullSyncAsync();

        var meta = await _db.SyncMetadata.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Type == "BinInventory.Source");

        return Ok(new
        {
            TotalSapRows      = report.TotalSapRows,
            DistinctItemCodes = report.DistinctItemCodes,
            RowsByWarehouse   = report.RowsByWarehouse,
            Upserted          = report.Upserted,
            Removed           = report.Removed,
            SyncTime          = report.SyncTime.ToString("yyyy-MM-dd HH:mm:ss UTC"),
            DurationSeconds   = Math.Round(report.DurationSeconds, 2),
            WatermarkAfter    = meta?.LastSyncedAt.ToString("yyyy-MM-dd HH:mm:ss UTC"),
            TotalCachedRows   = await _db.BinInventories.CountAsync(),
            Reconciliation    = new
            {
                report.Reconciliation.Checked,
                report.Reconciliation.Matched,
                report.Reconciliation.Mismatched
            }
        });
    }

    /// <summary>
    /// Trigger a delta sync — detects changed ItemCodes from OINM + OITM (ORDR excluded —
    /// SO commitment does not alter bin-level OnHandQty) since (last watermark − 2h),
    /// fetches current OIBQ snapshot for affected items, upserts and removes stale bin rows.
    /// Safe to call repeatedly — idempotent within the same watermark window.
    /// </summary>
    [HttpPost("sync/delta")]
    public async Task<IActionResult> DeltaSync()
    {
        _log.LogInformation("[BinInv] Manual delta sync triggered via API.");

        var watermarkBefore = (await _db.SyncMetadata.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Type == "BinInventory.Source"))
            ?.LastSyncedAt.ToString("yyyy-MM-dd HH:mm:ss UTC") ?? "(none)";

        var (upserted, removed) = await _sync.DeltaSyncAsync();

        var watermarkAfter = (await _db.SyncMetadata.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Type == "BinInventory.Source"))
            ?.LastSyncedAt.ToString("yyyy-MM-dd HH:mm:ss UTC");

        return Ok(new
        {
            WatermarkBefore = watermarkBefore,
            WatermarkAfter  = watermarkAfter,
            Upserted        = upserted,
            Removed         = removed,
            TotalCachedRows = await _db.BinInventories.CountAsync()
        });
    }

    /// <summary>
    /// Returns current BinInventory.Source watermark and row counts by warehouse.
    /// </summary>
    [HttpGet("status")]
    public async Task<IActionResult> Status()
    {
        var meta = await _db.SyncMetadata.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Type == "BinInventory.Source");

        var byWarehouse = await _db.BinInventories
            .Where(b => new[] { "001", "002", "003", "004" }.Contains(b.WhsCode))
            .GroupBy(b => b.WhsCode)
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
    /// Spot-check: returns all cached bin rows for an ItemCode plus a live SAP comparison.
    /// Useful for verifying VAG12776 or any known item. SUM(BinOnHand) per WhsCode is included.
    /// </summary>
    [HttpGet("spot-check/{itemCode}")]
    public async Task<IActionResult> SpotCheck(string itemCode)
    {
        var cached = await _db.BinInventories
            .Where(b => b.ItemCode == itemCode)
            .OrderBy(b => b.WhsCode)
            .ThenBy(b => b.BinCode)
            .ToListAsync();

        if (cached.Count == 0)
            return NotFound(new { Message = $"ItemCode '{itemCode}' not found in BinInventory cache." });

        var sumByWhs = cached
            .GroupBy(b => b.WhsCode)
            .Select(g => new { WhsCode = g.Key, BinSum = g.Sum(b => b.BinOnHand) })
            .OrderBy(g => g.WhsCode)
            .ToList();

        // Live SAP comparison
        var live = _sap.GetBinInventorySnapshotForItems(new[] { itemCode });

        return Ok(new
        {
            ItemCode       = itemCode,
            CachedBinCount = cached.Count,
            LiveBinCount   = live.Count,
            SumByWarehouse = sumByWhs,
            CachedRows = cached.Select(b => new
            {
                b.WhsCode,
                b.BinAbsEntry,
                b.BinCode,
                b.BinOnHand,
                b.LastUpdated
            }),
            LiveRows = live
                .Where(r => r.ItemCode.Equals(itemCode, StringComparison.OrdinalIgnoreCase))
                .OrderBy(r => r.WhsCode)
                .ThenBy(r => r.BinCode)
                .Select(r => new
                {
                    r.WhsCode,
                    r.BinAbsEntry,
                    r.BinCode,
                    r.BinOnHand
                })
        });
    }
}
