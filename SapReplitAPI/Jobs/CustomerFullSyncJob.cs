using Quartz;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;

[DisallowConcurrentExecution]
public class CustomerFullSyncJob : IJob
{
    private readonly CustomerCacheService _cacheService;
    private readonly ILogger<CustomerFullSyncJob> _logger;

    public CustomerFullSyncJob(CustomerCacheService cacheService, ILogger<CustomerFullSyncJob> logger)
    {
        _cacheService = cacheService;
        _logger = logger;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        _logger.LogInformation("📇 [CustomerFullSyncJob] Starting customer sync at {Time}", DateTime.Now);
        try
        {
            await _cacheService.FullSyncFromSAPAsync();
            _logger.LogInformation("✅ [CustomerFullSyncJob] Sync completed at {Time}", DateTime.Now);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ [CustomerFullSyncJob] Sync failed.");
            throw;
        }
    }
}
