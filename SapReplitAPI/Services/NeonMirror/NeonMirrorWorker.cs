using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SapReplitAPI.Models.NeonMirror;
using SapReplitAPI.Services.Inventory;

namespace SapReplitAPI.Services.NeonMirror;

/// <summary>
/// Independent BackgroundService — owns making Neon reflect already-durable local
/// state for everything in NeonMirrorWork. Deliberately has no SAP dependency at all
/// (SapService is never injected here): it only ever rereads SQLite and writes Neon.
///
/// Operationally independent of OutboxPollerService/SapEventOutbox — a slow or failing
/// Neon does not block SAP event capture/dispatch once the durable local state +
/// mirror-work row are safely committed (see InventoryEventRefreshService's durable-
/// fallback try/catch). This worker is what closes the loop afterward.
///
/// Poll interval / batch size: 500ms / 50, per the architecture review's evidence-based
/// recommendation — no Redis wake-up. At current measured production volume
/// (~9 events/hour average, 59/hour peak) this is far more headroom than needed;
/// tightening it further has no measured justification (see the decision report).
///
/// Concurrency: single instance, single claim loop — same "smallest safe change"
/// philosophy as SapEventOutbox. No parallel Neon writes for different items are
/// introduced by this phase; per-entity ordering is guaranteed by construction
/// (one worker, sequential claim-then-process, reread-current-state semantics).
/// </summary>
public sealed class NeonMirrorWorker : BackgroundService
{
    public const int DefaultPollMs   = 500;
    public const int DefaultBatch    = 50;
    public const int StaleProcessingMinutes = 5;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<NeonMirrorWorker> _log;

    public NeonMirrorWorker(IServiceScopeFactory scopeFactory, ILogger<NeonMirrorWorker> log)
    {
        _scopeFactory = scopeFactory;
        _log          = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation(
            "[NeonMirrorWorker] Starting. Poll interval: {Ms}ms, batch: {Batch}",
            DefaultPollMs, DefaultBatch);

        try
        {
            await using var startupScope = _scopeFactory.CreateAsyncScope();
            var repo = startupScope.ServiceProvider.GetRequiredService<NeonMirrorWorkRepository>();
            int reset = await repo.ResetStaleProcessingAsync(StaleProcessingMinutes, stoppingToken);
            if (reset > 0)
                _log.LogWarning(
                    "[NeonMirrorWorker] Startup recovery: reset {Count} stale Processing row(s) to Retrying.",
                    reset);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[NeonMirrorWorker] Startup stale-processing recovery failed — continuing.");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "[NeonMirrorWorker] Unexpected error in poll cycle — continuing after delay.");
            }

            try { await Task.Delay(DefaultPollMs, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        _log.LogInformation("[NeonMirrorWorker] Stopped.");
    }

    // Shutdown-timeline instrumentation: one stop-requested / stop-completed pair
    // with elapsed ms — not per-poll-cycle logging. This worker has no SAP
    // dependency (see class doc comment) and every Npgsql call it makes does
    // honor cancellation, so it should stop quickly; if it doesn't, that itself is
    // the finding.
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _log.LogInformation("🛑 [Shutdown] NeonMirrorWorker stop requested.");
        await base.StopAsync(cancellationToken);
        _log.LogInformation("🛑 [Shutdown] NeonMirrorWorker stop completed. ElapsedMs={Ms:F0}", sw.Elapsed.TotalMilliseconds);
    }

    private async Task RunCycleAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<NeonMirrorWorkRepository>();
        var inv  = scope.ServiceProvider.GetRequiredService<InventoryEventRefreshService>();

        var batch = await repo.ClaimBatchAsync(DefaultBatch, ct);
        if (batch.Count == 0) return;

        foreach (var work in batch)
        {
            if (ct.IsCancellationRequested) break;
            await ProcessOneAsync(repo, inv, work, ct);
        }

        _log.LogInformation("[NeonMirrorWorker] Cycle done: claimed={Count}", batch.Count);
    }

    private async Task ProcessOneAsync(
        NeonMirrorWorkRepository repo, InventoryEventRefreshService inv,
        NeonMirrorWork work, CancellationToken ct)
    {
        try
        {
            if (work.EntityType == NeonMirrorEntityType.InventorySnapshot)
            {
                // Poison-item isolation: any exception here is caught per-item — one bad
                // ItemCode goes to Retrying/DeadLetter on its own, the rest of the batch
                // is unaffected (the foreach in RunCycleAsync continues regardless).
                await inv.MirrorSingleItemToNeonAsync(work.EntityKey, ct);
                await repo.MarkDoneAsync(work.Id, ct);
            }
            else
            {
                // Forward-compatible: an EntityType this worker doesn't yet know how to
                // mirror is treated as a permanent, immediate failure rather than an
                // infinite retry loop or a silent drop.
                await repo.MarkRetryingAsync(work.Id,
                    $"Unknown EntityType '{work.EntityType}' — no mirror handler registered.", ct);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "[NeonMirrorWorker] Mirror failed EntityType={Type} EntityKey={Key} Attempt={Attempt}",
                work.EntityType, work.EntityKey, work.AttemptCount);
            await repo.MarkRetryingAsync(work.Id, $"{ex.GetType().Name}: {ex.Message}", ct);
        }
    }
}
