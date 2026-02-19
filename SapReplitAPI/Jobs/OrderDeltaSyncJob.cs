using Microsoft.Extensions.Logging;
using Quartz;
using System;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Threading.Tasks;

public class OrderDeltaSyncJob : IJob
{
    private readonly OrderCacheService _orderCacheService;
    private readonly ILogger<OrderDeltaSyncJob> _logger;

    public OrderDeltaSyncJob(OrderCacheService orderCacheService, ILogger<OrderDeltaSyncJob> logger)
    {
        _orderCacheService = orderCacheService;
        _logger = logger;
    }

    [SupportedOSPlatform("windows")]
    public async Task Execute(IJobExecutionContext context)
    {
        var jobKey = context.JobDetail.Key.Name;
        var start = DateTime.Now;
        var stopwatch = Stopwatch.StartNew();

        _logger.LogInformation("🔁 [{Job}] Starting delta sync at {Time}", jobKey, start);

        try
        {
            await _orderCacheService.SyncOrdersDeltaAsync();

            stopwatch.Stop();
            _logger.LogInformation("✅ [{Job}] Delta sync completed in {Duration}ms", jobKey, stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "❌ [{Job}] Failed after {Duration}ms", jobKey, stopwatch.ElapsedMilliseconds);
        }
    }
}