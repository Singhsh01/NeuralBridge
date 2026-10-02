using NeuralBridge.Application.Abstractions;

namespace NeuralBridge.Infrastructure.Realtime;

/// <summary>Async per-session mutex with reference counting, so idle sessions don't leak semaphores.</summary>
public sealed class SessionLockProvider : ISessionLockProvider
{
    private readonly Dictionary<Guid, RefCountedSemaphore> _locks = [];
    private readonly Lock _gate = new();

    public async Task<IAsyncDisposable> AcquireAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        RefCountedSemaphore semaphore;
        lock (_gate)
        {
            if (!_locks.TryGetValue(sessionId, out semaphore!))
            {
                semaphore = new RefCountedSemaphore();
                _locks[sessionId] = semaphore;
            }

            semaphore.References++;
        }

        try
        {
            await semaphore.Semaphore.WaitAsync(cancellationToken);
        }
        catch
        {
            Release(sessionId, semaphore, wasAcquired: false);
            throw;
        }

        return new Releaser(this, sessionId, semaphore);
    }

    /// <summary>Number of sessions with an outstanding lock (tests/diagnostics).</summary>
    public int ActiveLockCount
    {
        get
        {
            lock (_gate)
            {
                return _locks.Count;
            }
        }
    }

    private void Release(Guid sessionId, RefCountedSemaphore semaphore, bool wasAcquired)
    {
        if (wasAcquired)
        {
            semaphore.Semaphore.Release();
        }

        lock (_gate)
        {
            if (--semaphore.References == 0)
            {
                _locks.Remove(sessionId);
                semaphore.Semaphore.Dispose();
            }
        }
    }

    private sealed class RefCountedSemaphore
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public int References { get; set; }
    }

    private sealed class Releaser(SessionLockProvider owner, Guid sessionId, RefCountedSemaphore semaphore) : IAsyncDisposable
    {
        private int _released;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                owner.Release(sessionId, semaphore, wasAcquired: true);
            }

            return ValueTask.CompletedTask;
        }
    }
}
