using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SapReplitAPI.Models.Inventory;

namespace SapReplitAPI.Controllers;

/// <summary>
/// Warehouse-level inventory availability from the local SQLite cache.
/// Reads are informational/frontend only — SO stock validation continues to hit SAP live.
/// </summary>
[ApiController]
[Route("api/inventory")]
[ServiceFilter(typeof(SapReplitAPI.Filters.ApiKeyAuthFilter))]
public class InventoryController : ControllerBase
{
    private readonly CacheDbContext _db;
    private readonly ILogger<InventoryController> _log;

    public InventoryController(CacheDbContext db, ILogger<InventoryController> log)
    {
        _db  = db;
        _log = log;
    }

    /// <summary>
    /// Returns warehouse-level availability for the requested item across all warehouses.
    /// Source: SQLite WarehouseInventory cache (refreshed every 15 min via delta sync).
    /// Warehouses are ordered 001 → 002 → 003 → 004.
    /// </summary>
    [HttpGet("warehouse/{itemCode}")]
    public async Task<IActionResult> GetByItem(string itemCode)
    {
        var code = itemCode.Trim();

        var rows = await _db.WarehouseInventories
            .AsNoTracking()
            .Where(w => w.ItemCode == code)
            .OrderBy(w => w.WhsCode)
            .ToListAsync();

        if (rows.Count == 0)
            return NotFound(new { error = "Item warehouse inventory not found." });

        _log.LogInformation("[Inventory] warehouse/{ItemCode} — {Count} warehouse row(s) returned", code, rows.Count);

        var dtos = rows.Select(w => new WarehouseInventoryDto(
            w.WhsCode,
            w.WarehouseName,
            w.OnHand,
            w.IsCommitted,
            w.AvailableToSell,
            w.OnOrder,
            w.IsBinManaged,
            w.LastUpdated
        )).ToList();

        return Ok(new ItemWarehouseInventoryResponse(code, dtos));
    }

    /// <summary>
    /// Returns availability for one specific (ItemCode, WhsCode) pair.
    /// Useful for single-warehouse detail views or bin-readiness checks.
    /// </summary>
    [HttpGet("warehouse/{itemCode}/{whsCode}")]
    public async Task<IActionResult> GetByItemAndWarehouse(string itemCode, string whsCode)
    {
        var code = itemCode.Trim();
        var whs  = whsCode.Trim();

        var row = await _db.WarehouseInventories
            .AsNoTracking()
            .Where(w => w.ItemCode == code && w.WhsCode == whs)
            .FirstOrDefaultAsync();

        if (row is null)
            return NotFound(new { error = "Item warehouse inventory not found." });

        _log.LogInformation("[Inventory] warehouse/{ItemCode}/{WhsCode} — found", code, whs);

        return Ok(new WarehouseInventoryDto(
            row.WhsCode,
            row.WarehouseName,
            row.OnHand,
            row.IsCommitted,
            row.AvailableToSell,
            row.OnOrder,
            row.IsBinManaged,
            row.LastUpdated
        ));
    }
}
