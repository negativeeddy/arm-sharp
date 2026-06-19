namespace ArmRipper.Core.Models;

/// <summary>
/// Lightweight snapshot of mutable job fields for real-time SignalR broadcasts.
/// Sent from backend services whenever job state changes (progress, status, stage, etc.).
/// UI pages subscribe to the "JobUpdate" SignalR event and apply changes to the DOM.
/// </summary>
public class JobUpdate
{
    /// <summary>Job ID for routing to the correct UI element.</summary>
    public int JobId { get; set; }

    /// <summary>Current status as DB string (e.g. "ripping", "success").</summary>
    public string? Status { get; set; }

    /// <summary>Current pipeline stage (e.g. "identify", "rip", "transcode").</summary>
    public string? Stage { get; set; }

    /// <summary>MakeMKV rip progress 0–100, or null if not ripping.</summary>
    public int? MakeMkvProgress { get; set; }

    /// <summary>HandBrake/ffmpeg transcode progress 0–100, or null if not transcoding.</summary>
    public int? TranscodeProgress { get; set; }

    /// <summary>Human-readable description of current operation.</summary>
    public string? ProgressMessage { get; set; }

    /// <summary>Error messages, or null if none.</summary>
    public string? Errors { get; set; }

    /// <summary>Warning messages, or null if none.</summary>
    public string? Warnings { get; set; }

    /// <summary>Completion time in ISO 8601, or null if still running.</summary>
    public string? StopTime { get; set; }

    /// <summary>Formatted job duration (e.g. "0:14:10").</summary>
    public string? JobLength { get; set; }

    /// <summary>Final output directory path.</summary>
    public string? Path { get; set; }

    /// <summary>Display title.</summary>
    public string? Title { get; set; }

    /// <summary>Disc type for UI rendering.</summary>
    public string? DiscType { get; set; }

    /// <summary>Number of tracks/titles.</summary>
    public int? NoOfTitles { get; set; }

    /// <summary>Whether the tray was ejected.</summary>
    public bool Ejected { get; set; }

    /// <summary>Create a JobUpdate snapshot from a Job entity.</summary>
    public static JobUpdate FromJob(Job job) => new()
    {
        JobId = job.Id,
        Status = job.Status.ToDbString(),
        Stage = job.Stage.HasValue ? job.Stage.Value.ToClientString() : null,
        MakeMkvProgress = job.MakeMkvProgress,
        TranscodeProgress = job.TranscodeProgress,
        ProgressMessage = job.ProgressMessage,
        Errors = job.Errors,
        Warnings = job.Warnings,
        StopTime = job.StopTime?.ToString("o"),
        JobLength = job.JobLength,
        Path = job.Path,
        Title = job.Title,
        DiscType = job.DiscType.ToString(),
        NoOfTitles = job.NoOfTitles,
        Ejected = job.Ejected,
    };
}
