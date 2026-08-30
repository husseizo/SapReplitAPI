namespace SapReplitAPI.Services.Inventory;

/// <summary>
/// Singleton lock that serializes all SAP inventory reads + SQLite commits.
/// Window: WaitAsync() before SAP read → Release() after SQLite COMMIT.
/// Never hold simultaneously with NeonInventoryWriteCoordinator.
/// </summary>
public sealed class InventoryCacheWriteCoordinator
{
    private readonly SemaphoreSlim _sem = new(1, 1);

    public Task WaitAsync(CancellationToken ct = default) => _sem.WaitAsync(ct);
    public void Release() => _sem.Release();
}
