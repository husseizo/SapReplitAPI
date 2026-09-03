using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SapReplitAPI.Services.Events;

/// <summary>
/// Singleton BackgroundService. Polls SapEventOutbox every second.
/// Creates a fresh DI scope per event — never captures scoped services in the singleton.
/// Processes events sequentially (concurrency = 1) for Phase 1.
/// SAP DI API / COM is not proven safe for concurrent calls.
/// </summary>
public sealed class OutboxPollerService : BackgroundService
{
    private readonly IServiceScopeFactory   _scopeFactory;
    private readonly OutboxClaimService     _claim;
    private readonly ILogger<OutboxPollerService> _logger;

    private const int BatchSize   = 50;
    private const int PollDelayMs = 1_000;

    public OutboxPollerService(
        IServiceScopeFactory scopeFactory,
        OutboxClaimService   claim,
        ILogger<OutboxPollerService> logger)
    {
        _scopeFactory = scopeFactory;
        _claim        = claim;
        _logger       = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[OutboxPoller] Starting. Poll interval: {Ms}ms, batch: {Batch}",
            PollDelayMs, BatchSize);

        // Startup health probe — confirms DB reachability and reports initial queue depth.
        try
        {
            var h = await _claim.QueryHealthAsync(stoppingToken);
            _logger.LogInformation(
                "[EVENT-PIPELINE] MolasIntegrationConfigured=true OutboxPoller=ENABLED " +
                "DatabaseConnectivity=PASS PendingEvents={Pending} ProcessingEvents={Processing} " +
                "FailedEvents={Failed} TotalDone={Done} LastProcessedUtc={LastProcessed}",
                h.Pending, h.Processing, h.Failed, h.Done,
                h.LastProcessedUtc.HasValue ? h.LastProcessedUtc.Value.ToString("o") : "never");

            if (h.Failed > 0)
                _logger.LogWarning(
                    "[EVENT-PIPELINE] ALERT: {Failed} event(s) in permanent Failed state — inspect SapEventOutbox for LastError details.",
                    h.Failed);

            if (h.Pending > 100)
                _logger.LogWarning(
                    "[EVENT-PIPELINE] ALERT: Backlog={Pending} pending events — poller starting, processing now.",
                    h.Pending);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[EVENT-PIPELINE] MolasIntegrationConfigured=true OutboxPoller=ENABLED " +
                "DatabaseConnectivity=FAIL — poller will retry on first poll cycle.");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Stuck-lease recovery first — resets orphaned Processing rows
                await _claim.ResetOrphanedAsync(stoppingToken);

                var events = await _claim.ClaimBatchAsync(BatchSize, stoppingToken);

                if (events.Count > 0)
                    _logger.LogDebug("[OutboxPoller] Claimed {Count} event(s).", events.Count);

                // Sequential processing — no Task.WhenAll, no parallel DI API calls
                foreach (var ev in events)
                {
                    if (stoppingToken.IsCancellationRequested) break;
                    await ProcessEventAsync(ev, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[OutboxPoller] Unexpected error in poll loop — continuing after delay.");
            }

            try { await Task.Delay(PollDelayMs, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        _logger.LogInformation("[OutboxPoller] Stopped.");
    }

    private async Task ProcessEventAsync(SapOutboxEvent ev, CancellationToken ct)
    {
        await using var scope  = _scopeFactory.CreateAsyncScope();
        var router             = scope.ServiceProvider.GetRequiredService<EventHandlerRouter>();

        (bool ok, string? error) result;
        try
        {
            result = await router.HandleAsync(ev, ct);
        }
        catch (Exception ex)
        {
            // Handlers must not throw — this is a safety net
            result = (false, $"Unhandled exception: {ex.GetType().Name}: {ex.Message}");
            _logger.LogError(ex, "[OutboxPoller] Handler threw for EventId={EventId}", ev.EventId);
        }

        if (result.ok)
        {
            await _claim.MarkDoneAsync(ev.Id, ct);
            _logger.LogInformation(
                "[OutboxPoller] Done: {ObjectType}/{TransType} DocEntry={DocEntry} EventId={EventId} Attempt={Attempt} ClaimLag={Lag:F1}s",
                ev.ObjectType, ev.TransactionType, ev.DocEntry, ev.EventId, ev.AttemptCount,
                (DateTime.UtcNow - ev.CreatedAtUtc).TotalSeconds);
        }
        else
        {
            await _claim.MarkFailedAsync(ev.Id, ev.AttemptCount, result.error ?? "unknown error", ct);
        }
    }
}
