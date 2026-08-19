using Microsoft.AspNetCore.Mvc;
using SapReplitAPI.Services.Neon;
using SapReplitAPI.Services.Queue;
using System.Collections.Generic;
using System.Threading.Tasks;

[ApiController]
[Route("api/[controller]")]
public class ProductsController : ControllerBase
{
    private readonly SapService _sapService;
    private readonly ProductCacheService _cacheService;
    private readonly IBackgroundTaskQueue _taskQueue;
    private readonly NeonProductSyncService _neonSync;

    public ProductsController(SapService sapService, ProductCacheService cacheService, IBackgroundTaskQueue taskQueue, NeonProductSyncService neonSync)
    {
        _sapService = sapService;
        _cacheService = cacheService;
        _taskQueue = taskQueue;
        _neonSync = neonSync;
    }

    /// <summary>
    /// Returns paged cached product data from SQLite
    /// </summary>
    [HttpGet("cached")]
    public async Task<IActionResult> GetCached([FromQuery] int page = 1, [FromQuery] int pageSize = 2500)
    {
        try
        {
            var safePageSize = Math.Clamp(pageSize, 1, 500);
            var (total, paged) = await _cacheService.GetCachedProductsPageAsync(page, safePageSize);

            return Ok(new
            {
                TotalCount = total,
                Page = page,
                PageSize = safePageSize,
                Products = paged
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new
            {
                Message = "Failed to retrieve cached products",
                Error = ex.Message
            });
        }
    }


    /// <summary>
    /// Get a single product by ItemCode from cache (SQLite only).
    /// Set includeZero=true to return items with zero stock.
    /// </summary>
    [HttpGet("cached/by-itemcode/{itemCode}")]
    public async Task<IActionResult> GetCachedByItemCode(
        [FromRoute] string itemCode,
        [FromQuery] bool includeZero = false)
    {
        if (string.IsNullOrWhiteSpace(itemCode))
            return BadRequest(new { Message = "itemCode is required." });

        try
        {
            var dto = await _cacheService.GetCachedProductByItemCodeAsync(itemCode.Trim(), onlyWithStock: !includeZero);
            if (dto == null)
                return NotFound(new { Message = $"Product '{itemCode}' not found in cache{(includeZero ? "" : " (or stock <= 0)")}." });

            return Ok(dto);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new
            {
                Message = "Failed to retrieve cached product by itemCode.",
                Error = ex.Message
            });
        }
    }




    /// <summary>
    /// Performs a full sync: fetches from SAP and updates the local cache
    /// </summary>
    [HttpPost("sync")]
    public IActionResult SyncFromSAP()
    {
        _taskQueue.Enqueue(async (sp, token) =>
        {
            var scoped = sp.GetRequiredService<ProductCacheService>();
            Console.WriteLine("📦 Starting full product sync...");
            await scoped.FullSyncFromSAPAsync();
            Console.WriteLine("✅ Product sync complete.");
        });

        return Ok(new { Message = "🕓 Product full sync has been queued." });
    }

    /// <summary>
    /// Performs a delta sync for the current week's changes (Mon–Sun)
    /// </summary>
    [HttpPost("sync-products-delta")]
    public IActionResult SyncProductsDelta()
    {
        _taskQueue.Enqueue(async (sp, token) =>
        {
            var scoped = sp.GetRequiredService<ProductCacheService>();
            Console.WriteLine("📦 Starting delta product sync...");
            await scoped.SyncDeltaFromSAPAsync();
            Console.WriteLine("✅ Delta product sync complete.");
        });

        return Ok(new { Message = "🕓 Delta product sync has been queued." });
    }

    /// <summary>
    /// Manually push products from SQLite cache → Neon (full replace).
    /// Run this after a full SAP sync to clean frozen/inactive items from Neon.
    /// </summary>
    [HttpPost("sync-to-neon")]
    public IActionResult SyncProductsToNeon()
    {
        _taskQueue.Enqueue(async (sp, token) =>
        {
            var svc = sp.GetRequiredService<NeonProductSyncService>();
            Console.WriteLine("☁️ Starting product sync to Neon...");
            var count = await svc.FullReplaceAsync();
            Console.WriteLine($"✅ Products synced to Neon: {count}");
        });

        return Ok(new { Message = "☁️ Product sync to Neon queued." });
    }
}
