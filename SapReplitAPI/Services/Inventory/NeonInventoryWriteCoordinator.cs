namespace SapReplitAPI.Services.Inventory;

/// <summary>
/// Singleton lock that serializes all SQLite snapshot reads + Neon inventory commits.
/// Window: WaitAsync() before SQLite read → Release() after Neon COMMIT.
/// Must be acquired AFTER InventoryCacheWriteCoordinator is released — never hold both simultaneously.
/// </summary>
public sealed class NeonInventoryWriteCoordinator
{
    private readonly SemaphoreSlim _sem = new(1, 1);

    public Task WaitAsync(CancellationToken ct = default) => _sem.WaitAsync(ct);
    public void Release() => _sem.Release();
}
