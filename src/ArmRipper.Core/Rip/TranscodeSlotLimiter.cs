using ArmRipper.Core.Configuration;
using Microsoft.Extensions.Options;

namespace ArmRipper.Core.Rip;

public sealed class TranscodeSlotLimiter : ITranscodeSlotLimiter
{
    private readonly SemaphoreSlim? semaphore;

    public TranscodeSlotLimiter(IOptions<ArmSettings> settings)
    {
        var max = settings.Value.MaxConcurrentTranscodes;
        if (max > 0)
            semaphore = new SemaphoreSlim(max, max);
    }

    public async ValueTask<IAsyncDisposable> AcquireAsync(CancellationToken ct = default)
    {
        if (semaphore is null)
            return NoopLease.Instance;

        await semaphore.WaitAsync(ct);
        return new SemaphoreLease(semaphore);
    }

    private sealed class SemaphoreLease(SemaphoreSlim semaphore) : IAsyncDisposable
    {
        private readonly SemaphoreSlim semaphore = semaphore;
        private int disposed;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
                semaphore.Release();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class NoopLease : IAsyncDisposable
    {
        public static readonly NoopLease Instance = new();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
