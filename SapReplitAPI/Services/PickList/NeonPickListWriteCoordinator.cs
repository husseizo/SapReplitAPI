namespace SapReplitAPI.Services.PickList;

/// <summary>
/// Singleton lock that serializes all SQLite snapshot reads + Neon PickList commits.
/// Window: WaitAsync() before SQLite read → Release() after Neon COMMIT.
/// Pattern mirrors NeonInventoryWriteCoordinator — never hold simultaneously with it.
/// </summary>
public sealed class NeonPickListWriteCoordinator
{
    private readonly SemaphoreSlim _sem = new(1, 1);

    public Task WaitAsync(CancellationToken ct = default) => _sem.WaitAsync(ct);
    public void Release() => _sem.Release();
}
