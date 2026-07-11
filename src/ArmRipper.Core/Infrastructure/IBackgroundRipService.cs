namespace ArmRipper.Core.Infrastructure;

public interface IBackgroundRipService
{
    void StartRip(string devPath, CancellationToken ct = default);
    void StartForkedJob(int originalJobId, string rawFilePath, CancellationToken ct = default);

    /// <summary>
    /// Starts a standalone import transcode job for raw MKV files that came from another machine.
    /// Unlike StartForkedJob, this does not require an existing original job record — the caller
    /// provides the movie metadata directly.
    /// </summary>
    void StartImportJob(string rawFilePath, string title, string? year, string? videoType, string? discType, CancellationToken ct = default);

    void CancelRip(string devPath);

    /// <summary>Cancels ALL active rips and returns their dev paths.
    /// Used during graceful shutdown so jobs can be marked as resumable.</summary>
    IReadOnlyList<string> CancelAll();

    /// <summary>Number of currently active jobs.</summary>
    int ActiveCount { get; }

    /// <summary>
    /// Records a manual user-initiated eject on the given device so the
    /// eject-cooldown logic prevents any auto-rip on that device for the
    /// configured cooldown period.  Call this after a UI-triggered eject
    /// to prevent the drive from auto-closing and re-ripping.
    /// </summary>
    void RecordManualEject(string devPath);
}
