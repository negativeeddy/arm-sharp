namespace ArmRipper.Core.Rip;

public interface ITranscodeSlotLimiter
{
    ValueTask<IAsyncDisposable> AcquireAsync(CancellationToken ct = default);
}
