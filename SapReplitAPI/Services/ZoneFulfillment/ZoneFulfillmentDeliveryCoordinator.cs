using System.Collections.Concurrent;

namespace SapReplitAPI.Services.ZoneFulfillment;

/// <summary>
/// Singleton per-RequestId semaphore pool.
/// Prevents two concurrent POST /delivery requests for the same RequestId from both
/// passing "no DeliveryRecord found" and attempting concurrent ODLN.Add().
/// The UNIQUE(RequestId) constraint on DeliveryRecord is the DB backstop; this is
/// the process-level guard that avoids the unnecessary round-trips and SAP errors.
/// </summary>
public sealed class ZoneFulfillmentDeliveryCoordinator : IDisposable
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    public async Task<LockContext> AcquireAsync(Guid requestId, CancellationToken ct = default)
    {
        var sem = _locks.GetOrAdd(requestId, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct);
        return new LockContext(requestId, sem, _locks);
    }

    public void Dispose()
    {
        foreach (var s in _locks.Values)
            s.Dispose();
    }

    public sealed class LockContext : IDisposable
    {
        private readonly Guid                                    _requestId;
        private readonly SemaphoreSlim                           _sem;
        private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks;
        private bool _released;

        internal LockContext(
            Guid requestId,
            SemaphoreSlim sem,
            ConcurrentDictionary<Guid, SemaphoreSlim> locks)
        {
            _requestId = requestId;
            _sem       = sem;
            _locks     = locks;
        }

        public void Dispose()
        {
            if (_released) return;
            _released = true;
            _sem.Release();
            // Remove the semaphore when it is idle (count == 1 = no waiter).
            // If another waiter raced in, GetOrAdd returns the existing one, so removal is safe.
            if (_sem.CurrentCount == 1)
                _locks.TryRemove(_requestId, out _);
        }
    }
}
