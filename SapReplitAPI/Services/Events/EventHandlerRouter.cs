using Microsoft.Extensions.Logging;

namespace SapReplitAPI.Services.Events;

/// <summary>
/// Scoped service. Resolves the correct ISapEventHandler for an event and
/// delegates to it. Unmatched events are logged and marked Done (not failed)
/// because an unrecognised event type is not a processing error.
/// </summary>
public sealed class EventHandlerRouter
{
    private readonly IEnumerable<ISapEventHandler> _handlers;
    private readonly ILogger<EventHandlerRouter> _logger;

    public EventHandlerRouter(
        IEnumerable<ISapEventHandler> handlers,
        ILogger<EventHandlerRouter> logger)
    {
        _handlers = handlers;
        _logger   = logger;
    }

    public async Task<(bool ok, string? error)> HandleAsync(SapOutboxEvent ev, CancellationToken ct)
    {
        foreach (var handler in _handlers)
        {
            if (!handler.CanHandle(ev)) continue;

            _logger.LogInformation(
                "[EventRouter] Routing {ObjectType}/{TransType} DocEntry={DocEntry} EventId={EventId} to {Handler}",
                ev.ObjectType, ev.TransactionType, ev.DocEntry, ev.EventId,
                handler.GetType().Name);

            return await handler.HandleAsync(ev, ct);
        }

        // Defensive catch-all: log but do not fail events we cannot handle.
        // Phase 2 handlers will pick up inventory/commitment events when registered.
        _logger.LogWarning(
            "[EventRouter] No handler for ObjectType={ObjectType} TransType={TransType} DocEntry={DocEntry} EventId={EventId} — marking Done",
            ev.ObjectType, ev.TransactionType, ev.DocEntry, ev.EventId);

        return (true, null);
    }
}
