using Microsoft.Extensions.Logging;
using Quartz;
using SapReplitAPI.Services;
using System;
using System.Threading.Tasks;

[DisallowConcurrentExecution]
public class SyncTodayOrdersJob : IJob
{
    private readonly ILogger<SyncTodayOrdersJob> _logger;
    private readonly TodayOrderCacheService _todayService;

    public SyncTodayOrdersJob(ILogger<SyncTodayOrdersJob> logger, TodayOrderCacheService todayService)
    {
        _logger = logger;
        _todayService = todayService;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        _logger.LogInformation("🔄 Syncing today's orders using TodayOrderCacheService...");

        try
        {
            await _todayService.RefreshTodayOrdersFromSAP();
            _logger.LogInformation("✅ TodayOrderCacheService completed successfully.");
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("🛑 SyncTodayOrdersJob was cancelled.");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Error running TodayOrderCacheService.");
            throw;
        }
    }
}
