using Microsoft.Extensions.Logging;
using Quartz;

public class ProductChangeSyncJob : IJob
{
    private readonly SapService _sapService;
    private readonly CacheDbContext _db;
    private readonly ILogger<ProductChangeSyncJob> _logger;

    public ProductChangeSyncJob(SapService sapService, CacheDbContext db, ILogger<ProductChangeSyncJob> logger)
    {
        _sapService = sapService;
        _db = db;
        _logger = logger;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        var now = DateTime.Now;
        _logger.LogInformation("🔁 [ProductChangeSyncJob] Running at {Time}", now);

        var sapProducts = _sapService.GetLiveProducts();
        if (sapProducts == null || sapProducts.Count == 0)
        {
            _logger.LogWarning("⚠️ [ProductChangeSyncJob] No products returned from SAP.");
            return;
        }

        var changedProducts = sapProducts
            .Where(p => p.Warehouses.Sum(w => w.OnHandQty) > 0)
            .SelectMany(p => p.Warehouses.Select(w => new CachedProduct
            {
                ItemCode = p.ItemCode ?? string.Empty,
                ItemName = p.ItemName ?? string.Empty,
                U_Article_No = p.U_Article_No ?? string.Empty,
                U_MdlTEST = p.U_MdlTEST ?? string.Empty,
                Price = p.Price,
                Price05 = p.Price05,
                OnHand = w.OnHandQty,
                WhsCode = w.WarehouseCode ?? "UNKNOWN",
                LastUpdated = now,
                U_Item_Name = p.U_Item_Name ?? string.Empty
            }))
            .Where(cp =>
                cp.OnHand > 0 &&
                !_db.Products.Any(dbp =>
                    dbp.ItemCode == cp.ItemCode &&
                    dbp.WhsCode == cp.WhsCode &&
                    (dbp.OnHand != cp.OnHand || dbp.Price != cp.Price)
                ))
            .ToList();

        _logger.LogInformation("📦 [ProductChangeSyncJob] Found {Count} changed products", changedProducts.Count);

        if (changedProducts.Count > 0)
        {
            _db.Products.AddRange(changedProducts);
            await _db.SaveChangesAsync();
            _logger.LogInformation("✅ [ProductChangeSyncJob] Updated cache with {Count} product entries", changedProducts.Count);
        }
        else
        {
            _logger.LogInformation("ℹ️ [ProductChangeSyncJob] No new product changes to cache.");
        }
    }
}