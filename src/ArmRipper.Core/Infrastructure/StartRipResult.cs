namespace ArmRipper.Core.Infrastructure;

/// <summary>
/// Result returned by <see cref="IBackgroundRipService.StartRip"/> indicating
/// whether the rip was accepted or rejected, and why.
/// </summary>
public sealed class StartRipResult
{
    private StartRipResult() { }

    /// <summary>The rip was accepted and a pipeline has been started.</summary>
    public static readonly StartRipResult Accepted = new() { IsAccepted = true };

    /// <summary>The rip was rejected because the drive is busy ripping another disc.</summary>
    public static StartRipResult DriveBusy(string devPath) =>
        new() { IsAccepted = false, RejectionReason = $"Cannot start rip on {devPath} — the drive is currently busy ripping another disc." };

    /// <summary>The rip was rejected for another reason (e.g. still in eject cooldown).</summary>
    public static StartRipResult Rejected(string reason) =>
        new() { IsAccepted = false, RejectionReason = reason };

    public bool IsAccepted { get; private init; }
    public bool IsRejected => !IsAccepted;
    public string? RejectionReason { get; private init; }
}
