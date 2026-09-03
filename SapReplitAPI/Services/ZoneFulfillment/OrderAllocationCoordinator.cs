namespace SapReplitAPI.Services.ZoneFulfillment;

/// <summary>
/// Singleton SemaphoreSlim(1,1) that serializes the critical section:
///   OITW read → allocate → persist allocation plan → SAP ORDR.Add() → persist SoLineFragments.
///
/// IMPORTANT: This coordinator must NEVER be held at the same time as
/// NeonInventoryWriteCoordinator or InventoryCacheWriteCoordinator.
/// Acquire this coordinator first; release before any inventory-cache write.
/// </summary>
public sealed class OrderAllocationCoordinator : IDisposable
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    /// <summary>
    /// Acquires the lock. Caller must await the returned context and dispose it to release.
    /// Throws OperationCanceledException if ct is cancelled before the lock is acquired.
    /// </summary>
    public async Task<LockContext> AcquireAsync(CancellationToken ct = default)
    {
        await _semaphore.WaitAsync(ct);
        return new LockContext(_semaphore);
    }

    public void Dispose() => _semaphore.Dispose();

    public sealed class LockContext : IDisposable
    {
        private readonly SemaphoreSlim _sem;
        private bool _released;

        internal LockContext(SemaphoreSlim sem) => _sem = sem;

        public void Dispose()
        {
            if (_released) return;
            _released = true;
            _sem.Release();
        }
    }
}
