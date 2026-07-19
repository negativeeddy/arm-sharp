namespace ArmRipper.Core.Rip;

/// <summary>
/// Global concurrency gate for transcode processes.  Unlike a fixed
/// <see cref="SemaphoreSlim"/>, this limiter reads the effective
/// <c>MaxConcurrentTranscodes</c> from each caller at acquire time so
/// that UI-driven settings changes take effect without a restart.
/// </summary>
public sealed class TranscodeSlotLimiter : ITranscodeSlotLimiter
{
    private readonly object _gate = new();
    private int _activeCount;
    private readonly Queue<Waiter> _waitQueue = new();

    public ValueTask<IAsyncDisposable> AcquireAsync(int maxConcurrent, CancellationToken ct = default)
    {
        // 0 or negative → no limiting
        if (maxConcurrent <= 0)
            return new ValueTask<IAsyncDisposable>(NoopLease.Instance);

        lock (_gate)
        {
            if (_activeCount < maxConcurrent)
            {
                _activeCount++;
                return new ValueTask<IAsyncDisposable>(new Lease(this));
            }

            var waiter = new Waiter();
            _waitQueue.Enqueue(waiter);

            // Register cancellation: when the token fires, mark the waiter
            // as done so ReleaseOne skips it.  If ReleaseOne already claimed
            // it, TrySetCanceled is a no-op.
            if (ct.CanBeCanceled)
            {
                waiter.Cancellation = ct.Register(static state =>
                {
                    var w = (Waiter)state!;
                    // Atomically mark as done; if ReleaseOne hasn't claimed it yet, cancel the TCS.
                    if (Interlocked.Exchange(ref w.Done, 1) == 0)
                        w.Tcs.TrySetCanceled();
                }, waiter);
            }

            return new ValueTask<IAsyncDisposable>(waiter.Tcs.Task);
        }
    }

    /// <summary>Called by <see cref="Lease.DisposeAsync"/> when a transcode finishes.</summary>
    private void ReleaseOne()
    {
        while (true)
        {
            Waiter? next = null;
            lock (_gate)
            {
                while (_waitQueue.Count > 0)
                {
                    var w = _waitQueue.Dequeue();
                    // Try to claim this waiter before it's cancelled
                    if (Interlocked.Exchange(ref w.Done, 1) == 0)
                    {
                        next = w;
                        break;
                    }
                    // Already cancelled — clean up its registration
                    w.Cancellation.Dispose();
                }

                if (next is null)
                {
                    // No waiters — just release the slot
                    _activeCount--;
                    return;
                }
            }

            // Dispose the cancellation registration now that we own the waiter
            next.Cancellation.Dispose();

            if (next.Tcs.TrySetResult(new Lease(this)))
                return;
            // If TrySetResult failed (extremely unlikely race), loop and try next waiter
        }
    }

    private sealed class Waiter
    {
        public TaskCompletionSource<IAsyncDisposable> Tcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenRegistration Cancellation;
        /// <summary>
        /// 0 = waiting, 1 = done (either fulfilled by ReleaseOne or cancelled).
        /// </summary>
        public int Done;
    }

    private sealed class Lease(TranscodeSlotLimiter limiter) : IAsyncDisposable
    {
        private int _disposed;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                limiter.ReleaseOne();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class NoopLease : IAsyncDisposable
    {
        public static readonly NoopLease Instance = new();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
