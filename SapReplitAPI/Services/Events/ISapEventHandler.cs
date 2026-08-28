namespace SapReplitAPI.Services.Events;

public interface ISapEventHandler
{
    bool CanHandle(SapOutboxEvent ev);

    /// <summary>
    /// Processes the event. Returns (true, null) on success,
    /// (false, errorMessage) on failure. Never throws — all exceptions
    /// must be caught internally and surfaced as (false, message).
    /// </summary>
    Task<(bool ok, string? error)> HandleAsync(SapOutboxEvent ev, CancellationToken ct);
}
