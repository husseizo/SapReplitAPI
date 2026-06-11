using Quartz;
using Serilog;
using System;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Threading.Tasks;

[DisallowConcurrentExecution]
public class OrderFullSyncJob : IJob
{
    private readonly OrderCacheService _orderService;
    private readonly Serilog.ILogger _logger;

    public OrderFullSyncJob(OrderCacheService orderService)
    {
        _orderService = orderService;
        _logger = Log.ForContext<OrderFullSyncJob>();
    }

    [SupportedOSPlatform("windows")]
    public async Task Execute(IJobExecutionContext context)
    {
        _logger.Information("⏳ [OrderFullSyncJob] Starting full order sync job...");

        var sw = Stopwatch.StartNew();

        try
        {
            await _orderService.FullSyncOrdersAsync();

            sw.Stop();
            _logger.Information("✅ [OrderFullSyncJob] Full sync completed in {ElapsedSeconds} sec.",
                sw.Elapsed.TotalSeconds.ToString("0.00"));
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.Error(ex, "❌ [OrderFullSyncJob] Failed after {ElapsedSeconds} sec.",
                sw.Elapsed.TotalSeconds.ToString("0.00"));
            throw;
        }
    }
}
