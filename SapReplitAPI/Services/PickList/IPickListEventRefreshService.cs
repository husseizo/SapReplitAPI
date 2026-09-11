namespace SapReplitAPI.Services.PickList;

/// <summary>
/// Contract for the event-driven PickList cache fast path.
/// Returns (true, null) on success; (false, error) on failure.
/// Non-fatal contract: callers must not roll back SAP/Molas state on failure.
/// </summary>
public interface IPickListEventRefreshService
{
    Task<(bool ok, string? error)> RefreshAsync(int absEntry, CancellationToken ct);
}
