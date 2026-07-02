namespace ArmRipper.Core.Infrastructure;

public interface IBackgroundRipService
{
    void StartRip(string devPath, CancellationToken ct = default);
    void StartForkedJob(int originalJobId, string rawFilePath, CancellationToken ct = default);
    void CancelRip(string devPath);

    /// <summary>Cancels ALL active rips and returns their dev paths.
    /// Used during graceful shutdown so jobs can be marked as resumable.</summary>
    IReadOnlyList<string> CancelAll();

    /// <summary>Number of currently active jobs.</summary>
    int ActiveCount { get; }
}
