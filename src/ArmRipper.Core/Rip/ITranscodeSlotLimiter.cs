namespace ArmRipper.Core.Rip;

public interface ITranscodeSlotLimiter
{
    /// <summary>
    /// Acquires a transcode slot, respecting the effective max-concurrent limit.
    /// </summary>
    /// <param name="maxConcurrent">Maximum concurrent transcodes allowed.
    /// Use 0 or negative to disable limiting entirely.</param>
    ValueTask<IAsyncDisposable> AcquireAsync(int maxConcurrent, CancellationToken ct = default);
}
