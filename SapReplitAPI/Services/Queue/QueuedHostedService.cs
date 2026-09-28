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

        // Shutdown-timeline instrumentation only — the currently in-flight work
        // item's name and start time, so a slow stop can be explained precisely
        // (e.g. a SAP-bound sync task that ignores stoppingToken internally: the
        // token is threaded through to workItem, but individual enqueued lambdas
        // are not guaranteed to honor it once inside a synchronous SAP DI API call
        // — see the Real-Time Neon Foundation shutdown-lifecycle investigation).
        private volatile string? _inFlightTask;
        private DateTime _inFlightStartedUtc;

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
                        _inFlightTask = workItem.Method.Name;
                        _inFlightStartedUtc = DateTime.UtcNow;
                        _logger.Information("🚀 Executing background task: {Task}", workItem.Method.Name);
                        await workItem(provider, stoppingToken);
                        _logger.Information("✅ Completed background task: {Task}", workItem.Method.Name);
                        _inFlightTask = null;
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

        // Shutdown-timeline instrumentation: one stop-requested / stop-completed
        // pair with elapsed ms, plus what work item (if any) was still running when
        // the stop signal arrived — not per-loop logging.
        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            var task = _inFlightTask;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            if (task != null)
                _logger.Warning(
                    "🛑 [Shutdown] QueuedHostedService stop requested while {Task} in flight (running {Ms:F0}ms so far) — " +
                    "not safely cancellable mid-task; waiting for it to return on its own, for an indeterminate " +
                    "duration (no enforced timeout). Not force-killed.",
                    task, (DateTime.UtcNow - _inFlightStartedUtc).TotalMilliseconds);
            else
                _logger.Information("🛑 [Shutdown] QueuedHostedService stop requested — idle.");

            await base.StopAsync(cancellationToken);

            _logger.Information("🛑 [Shutdown] QueuedHostedService stop completed. ElapsedMs={Ms:F0}", sw.Elapsed.TotalMilliseconds);
        }
    }
}