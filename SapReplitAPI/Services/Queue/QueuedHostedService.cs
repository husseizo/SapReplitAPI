using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace SapReplitAPI.Services.Queue
{
    public class QueuedHostedService : BackgroundService
    {
        private readonly IBackgroundTaskQueue _taskQueue;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly Serilog.ILogger _logger = Log.ForContext<QueuedHostedService>();

        public QueuedHostedService(IBackgroundTaskQueue taskQueue, IServiceScopeFactory scopeFactory)
        {
            _taskQueue = taskQueue;
            _scopeFactory = scopeFactory;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.Information("📥 QueuedHostedService is starting.");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var workItem = await _taskQueue.DequeueAsync(stoppingToken);

                    using var scope = _scopeFactory.CreateScope();
                    var provider = scope.ServiceProvider;

                    // Optional: log scoped execution
                    using (Serilog.Context.LogContext.PushProperty("BackgroundTask", workItem.Method.Name))
                    {
                        _logger.Information("🚀 Executing background task: {Task}", workItem.Method.Name);
                        await workItem(provider, stoppingToken);
                        _logger.Information("✅ Completed background task: {Task}", workItem.Method.Name);
                    }
                }
                catch (OperationCanceledException)
                {
                    _logger.Warning("⚠️ QueuedHostedService cancellation was requested.");
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "❌ Unexpected error while executing background task.");
                }
            }

            _logger.Information("🛑 QueuedHostedService is stopping.");
        }
    }
}