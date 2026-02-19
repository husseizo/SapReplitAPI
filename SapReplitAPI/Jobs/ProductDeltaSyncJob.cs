using Quartz;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;

public class ProductDeltaSyncJob : IJob
{
    private readonly ProductCacheService _productCache;
    private readonly ILogger<ProductDeltaSyncJob> _logger;

    public ProductDeltaSyncJob(ProductCacheService productCache, ILogger<ProductDeltaSyncJob> logger)
    {
        _productCache = productCache;
        _logger = logger;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        _logger.LogInformation("🔄 [ProductDeltaSyncJob] Triggered at {Time}", DateTime.Now);

        try
        {
            await _productCache.SyncDeltaFromSAPAsync();
            _logger.LogInformation("✅ [ProductDeltaSyncJob] Delta sync completed successfully at {Time}", DateTime.Now);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ [ProductDeltaSyncJob] Failed during delta sync");
        }
    }
}