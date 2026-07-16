using ArmRipper.Core.Configuration;
using ArmRipper.Core.Infrastructure.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ArmRipper.Core.Rip;

/// <summary>
/// Gating mechanism that limits the number of concurrent transcode processes.
///
/// Unlike the old <see cref="SemaphoreSlim"/>-based implementation, this limiter
/// reads the effective <c>MaxConcurrentTranscodes</c> setting (YAML file + DB
/// overrides) on every <see cref="AcquireAsync"/> call so that changes made via
/// the Settings UI take effect without restarting the app.
///
/// The internal gate uses a count-and-queue pattern so the limit can grow and
/// shrink dynamically. Existing leases are never preempted; only future acquires
/// are gated by the current limit.
/// </summary>
public sealed class TranscodeSlotLimiter : ITranscodeSlotLimiter
{
    private readonly IOptions<ArmSettings> _fileSettings;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly object _lock = new();
    private int _activeCount;
    private readonly Queue<TaskCompletionSource<bool>> _waiters = new();

    public TranscodeSlotLimiter(IOptions<ArmSettings> fileSettings, IServiceScopeFactory scopeFactory)
    {
        _fileSettings = fileSettings;
        _scopeFactory = scopeFactory;
    }

    public async ValueTask<IAsyncDisposable> AcquireAsync(CancellationToken ct = default)
    {
        // Read the effective MaxConcurrentTranscodes (YAML file + DB overrides)
        int max;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ArmDbContext>();
            var effective = await SettingsHelper.GetEffectiveSettingsAsync(db, _fileSettings.Value, ct);
            max = effective.MaxConcurrentTranscodes;
        }

        // 0 or negative means unlimited — bypass the gate entirely
        if (max <= 0)
            return NoopLease.Instance;

        TaskCompletionSource<bool>? waiter = null;
        lock (_lock)
        {
            if (_activeCount < max)
            {
                _activeCount++;
                return new DynamicLease(this);
            }

            waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Enqueue(waiter);
        }

        // Register cancellation so that if the caller gives up, the Release
        // loop skips this waiter and hands the slot to the next one in line.
        using var ctr = ct.Register(static state =>
        {
            var tcs = (TaskCompletionSource<bool>)state!;
            tcs.TrySetCanceled();
        }, waiter);

        try
        {
            await waiter.Task;
        }
        catch (OperationCanceledException)
        {
            throw;
        }

        return new DynamicLease(this);
    }

    internal void Release()
    {
        lock (_lock)
        {
            // Hand the released slot to the next non-cancelled waiter
            while (_waiters.Count > 0)
            {
                var next = _waiters.Dequeue();
                if (next.TrySetResult(true))
                {
                    // Waiter takes over the slot — activeCount unchanged
                    return;
                }
                // Waiter was cancelled — skip and try the next one
            }

            // No waiters — genuinely free the slot
            _activeCount--;
        }
    }

    private sealed class DynamicLease(TranscodeSlotLimiter limiter) : IAsyncDisposable
    {
        private int _disposed;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                limiter.Release();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class NoopLease : IAsyncDisposable
    {
        public static readonly NoopLease Instance = new();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
