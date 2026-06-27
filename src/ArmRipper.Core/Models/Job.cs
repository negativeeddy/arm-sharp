using ArmRipper.Core.Configuration;
using System.ComponentModel.DataAnnotations.Schema;

namespace ArmRipper.Core.Models;

public class Job
{
    public int Id { get; init; }
    public string ArmVersion { get; set; } = string.Empty;
    public string? CrcId { get; set; }
    public string? LogFile { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime? StopTime { get; set; }
    public string? JobLength { get; set; }
    public JobState Status { get; set; } = JobState.Active;
    /// <summary>Current pipeline stage.</summary>
    public RipStage? Stage { get; set; }
    public int? NoOfTitles { get; set; }
    public string? Title { get; set; }
    public string? TitleAuto { get; set; }
    public string? TitleManual { get; set; }
    public string? Year { get; set; }
    public string? YearAuto { get; set; }
    public string? YearManual { get; set; }
    public string? VideoType { get; set; }
    public string? VideoTypeAuto { get; set; }
    public string? VideoTypeManual { get; set; }
    public string? ImdbId { get; set; }
    public string? ImdbIdAuto { get; set; }
    public string? ImdbIdManual { get; set; }
    public string? PosterUrl { get; set; }
    public string? PosterUrlAuto { get; set; }
    public string? PosterUrlManual { get; set; }
    public string? DevPath { get; set; }
    public string? MountPoint { get; set; }
    public bool HasNiceTitle { get; set; }
    public string? Errors { get; set; }
    public string? Warnings { get; set; }
    public DiscType DiscType { get; set; } = DiscType.Unknown;
    public string? Label { get; set; }
    public string? Path { get; set; }
    public bool Ejected { get; set; }
    public int? Pid { get; set; }
    public string? PidHash { get; set; }
    public bool IsIso { get; set; }
    public bool ManualStart { get; set; }
    public bool ManualMode { get; set; }
    /// <summary>Set to true by the UI to signal the Conductor to exit the manual wait loop early.</summary>
    public bool ManualWaitResume { get; set; }
    public bool HasTrack99 { get; set; }
    public string? DiscFingerprint { get; set; }
    /// <summary>If set, this job was forked from another job (e.g. a re-transcode of raw files).</summary>
    public int? OriginalJobId { get; set; }

    /// <summary>Transient — current MakeMKV rip percentage (0–100). Goes over SignalR, NOT persisted to DB.</summary>
    [NotMapped]
    public int? MakeMkvProgress { get; set; }

    /// <summary>Transient — current transcode percentage (0–100). Goes over SignalR, NOT persisted to DB.</summary>
    [NotMapped]
    public int? TranscodeProgress { get; set; }

    /// <summary>Transient — human-readable description of current operation. Goes over SignalR, NOT persisted to DB.</summary>
    [NotMapped]
    public string? ProgressMessage { get; set; }

    public string? StageErrors { get; set; }

    [NotMapped]
    public int? TitleCount
    {
        get => NoOfTitles;
        set => NoOfTitles = value;
    }

    /// <summary>
    /// Pipe-delimited list of completed stage names (e.g. "setup|identify|rip").
    /// Used for idempotency — before running a stage, check if it is already in this list.
    /// </summary>
    public string? CompletedStages { get; set; }

    public ICollection<Track> Tracks { get; init; } = new List<Track>();
    public ConfigSnapshot? Config { get; set; }

    /// <summary>Full path to the on-disk log file for this job.</summary>
    public string GetLogFilePath(string? defaultLogPath = null)
    {
        var logPath = Config?.LogPath ?? defaultLogPath ?? ArmPaths.DefaultLogPath;
        return System.IO.Path.Combine(logPath, LogFile ?? $"{Id}.log");
    }

    /// <summary>Returns true if the given stage has already been completed.</summary>
    public bool IsStageComplete(RipStage stage)
    {
        if (string.IsNullOrEmpty(CompletedStages))
            return false;
        var name = stage.ToString();
        return CompletedStages.Split('|', StringSplitOptions.RemoveEmptyEntries)
            .Any(s => s.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Marks a stage as completed. Appends to CompletedStages if not already present.</summary>
    public void MarkStageComplete(RipStage stage)
    {
        var name = stage.ToString();
        var stages = string.IsNullOrEmpty(CompletedStages)
            ? Array.Empty<string>()
            : CompletedStages.Split('|', StringSplitOptions.RemoveEmptyEntries);

        if (stages.Any(s => s.Equals(name, StringComparison.OrdinalIgnoreCase)))
            return;

        CompletedStages = string.IsNullOrEmpty(CompletedStages)
            ? name
            : $"{CompletedStages}|{name}";
    }
}
