namespace ArmRipper.Core.Infrastructure;

public interface IBackgroundRipService
{
    /// <summary>
    /// Attempts to start a rip on the given optical drive.
    /// Returns <see cref="StartRipResult"/> indicating whether the rip was accepted
    /// (a pipeline was started) or rejected (with a reason).
    /// </summary>
    StartRipResult StartRip(string devPath, CancellationToken ct = default);
    void StartForkedJob(int originalJobId, string rawFilePath, CancellationToken ct = default);

    /// <summary>
    /// Starts a standalone import transcode job for raw MKV files that came from another machine.
    /// Unlike StartForkedJob, this does not require an existing original job record — the caller
    /// provides the movie metadata directly.
    /// Creates the job in the DB synchronously and returns its ID so the caller can redirect
    /// to the job detail page. The actual transcode runs in the background.
    /// </summary>
    int StartImportJob(string rawFilePath, string title, string? year, string? videoType, string? discType, CancellationToken ct = default);

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
