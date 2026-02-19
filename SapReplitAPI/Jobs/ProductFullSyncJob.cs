using Quartz;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;

public class ProductFullSyncJob : IJob
{
    private readonly ProductCacheService _productCache;
    private readonly ILogger<ProductFullSyncJob> _logger;

    public ProductFullSyncJob(ProductCacheService productCache, ILogger<ProductFullSyncJob> logger)
    {
        _productCache = productCache;
        _logger = logger;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        _logger.LogInformation("🌙 [ProductFullSyncJob] Triggered full sync at {Time}", DateTime.Now);

        try
        {
            var result = await _productCache.FullSyncFromSAPAsync();
            _logger.LogInformation("✅ [ProductFullSyncJob] Completed full sync at {Time}. Result: {@Result}", DateTime.Now, result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ [ProductFullSyncJob] Failed during full sync");
        }
    }
}