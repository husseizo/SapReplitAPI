using Quartz;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SapReplitAPI.Services;

[DisallowConcurrentExecution]
public class SyncOpenOrdersJob : IJob
{
    private readonly OpenOrderCacheService _service;
    private readonly ILogger<SyncOpenOrdersJob> _logger;

    public SyncOpenOrdersJob(OpenOrderCacheService service, ILogger<SyncOpenOrdersJob> logger)
    {
        _service = service;
        _logger = logger;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        _logger.LogInformation("📋 [SyncOpenOrdersJob] Starting sync...");
        try
        {
            await _service.SyncOpenOrdersAsync();
            _logger.LogInformation("✅ [SyncOpenOrdersJob] Completed successfully.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ [SyncOpenOrdersJob] Failed.");
            throw;
        }
    }
}
