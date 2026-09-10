namespace SapReplitAPI.Services.TodayOrders;

/// <summary>
/// Singleton lock that serializes Neon TodayOrder writes between:
/// - targeted event-driven single-DocEntry writes (fast path)
/// - full table replace in NeonSyncJob (reconciliation path)
///
/// Window: WaitAsync() before Neon DELETE/INSERT → Release() after Neon COMMIT.
/// Pattern mirrors NeonInventoryWriteCoordinator and NeonPickListWriteCoordinator.
/// </summary>
public sealed class NeonTodayOrderWriteCoordinator
{
    private readonly SemaphoreSlim _sem = new(1, 1);

    public Task WaitAsync(CancellationToken ct = default) => _sem.WaitAsync(ct);
    public void Release() => _sem.Release();
}
