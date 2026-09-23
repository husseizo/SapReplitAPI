using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SapReplitAPI.Filters;
using SapReplitAPI.Services.Neon;
using SapReplitAPI.Services.Product;
using SapReplitAPI.Services.Queue;
using System.Collections.Generic;
using System.Threading.Tasks;

[ApiController]
[Route("api/[controller]")]
[ServiceFilter(typeof(ApiKeyAuthFilter))]
public class ProductsController : ControllerBase
{
    private readonly SapService _sapService;
    private readonly ProductCacheService _cacheService;
    private readonly IBackgroundTaskQueue _taskQueue;
    private readonly NeonProductSyncService _neonSync;
    private readonly ProductPriceListSyncService _priceSync;
    private readonly CacheDbContext _sqlite;

    public ProductsController(SapService sapService, ProductCacheService cacheService,
        IBackgroundTaskQueue taskQueue, NeonProductSyncService neonSync,
        ProductPriceListSyncService priceSync, CacheDbContext sqlite)
    {
        _sapService = sapService;
        _cacheService = cacheService;
        _taskQueue = taskQueue;
        _neonSync = neonSync;
        _priceSync = priceSync;
        _sqlite = sqlite;
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

    /// <summary>
    /// Returns all cached price lists (OPLN master data).
    /// GET /api/products/price-lists
    /// </summary>
    [HttpGet("price-lists")]
    public async Task<IActionResult> GetPriceLists(CancellationToken ct)
    {
        var rows = await _sqlite.PriceLists.AsNoTracking()
            .OrderBy(p => p.PriceListNum)
            .Select(p => new
            {
                p.PriceListNum,
                p.PriceListName,
                p.Currency,
                p.IsActive,
                p.Factor,
                p.BasePriceList,
            })
            .ToListAsync(ct);
        return Ok(rows);
    }

    /// <summary>
    /// Returns all cached price list prices for one item (from ItemPriceLists).
    /// GET /api/products/{itemCode}/price-lists
    /// </summary>
    [HttpGet("{itemCode}/price-lists")]
    public async Task<IActionResult> GetItemPriceLists([FromRoute] string itemCode, CancellationToken ct)
    {
        var rows = await _sqlite.ItemPriceLists.AsNoTracking()
            .Where(i => i.ItemCode == itemCode)
            .OrderBy(i => i.PriceListNum)
            .Select(i => new
            {
                i.ItemCode,
                i.PriceListNum,
                i.Price,
                i.Currency,
                i.LastUpdatedUtc,
            })
            .ToListAsync(ct);
        if (rows.Count == 0) return NotFound(new { Message = $"No price list data cached for item '{itemCode}'." });
        return Ok(rows);
    }

    /// <summary>
    /// Triggers a full price list sync: SAP OPLN + ITM1 → SQLite PriceLists/ItemPriceLists → Products projection → Neon.
    /// POST /api/products/sync-price-lists
    /// </summary>
    [HttpPost("sync-price-lists")]
    public IActionResult SyncPriceLists()
    {
        _taskQueue.Enqueue(async (sp, token) =>
        {
            var svc = sp.GetRequiredService<ProductPriceListSyncService>();
            Console.WriteLine("[PriceSync] Full price list sync queued — starting...");
            var result = await svc.FullSyncAsync(token);
            Console.WriteLine($"[PriceSync] Done. SQLite: {result.SqlitePriceListRows} PLists, " +
                              $"{result.SqliteItemPriceRows} ItemPrices, {result.SqliteProjectedRows} Products projected. " +
                              $"Neon: {result.NeonPriceListRows}/{result.NeonItemPriceRows}/{result.NeonProjectedRows}. " +
                              $"{result.ElapsedSeconds:0.0}s");
        });
        return Accepted(new { Message = "Full price list sync queued." });
    }

    /// <summary>
    /// Returns per-item per-PL integrity status (SQLite IPL vs Products projection vs Neon).
    /// GET /api/products/price-integrity?itemCode=BM10001&itemCode=XYZ
    /// </summary>
    [HttpGet("price-integrity")]
    public async Task<IActionResult> GetPriceIntegrity(
        [FromQuery] List<string>? itemCode, CancellationToken ct)
    {
        var rows = await _priceSync.CheckIntegrityAsync(itemCode?.Count > 0 ? itemCode : null, ct);
        var summary = new
        {
            TotalRows    = rows.Count,
            InSync       = rows.Count(r => r.Status == "IN_SYNC"),
            Issues       = rows.Where(r => r.Status != "IN_SYNC").ToList(),
        };
        return Ok(summary);
    }
}
