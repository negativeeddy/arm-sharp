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

    /// <summary>When the current stage started, in ISO 8601 format.</summary>
    public string? StageStartTime { get; set; }

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

    /// <summary>Auto-detected title (set during identification).</summary>
    public string? TitleAuto { get; set; }

    /// <summary>Auto-detected release year.</summary>
    public string? YearAuto { get; set; }

    /// <summary>Auto-detected content type (movie/series/tv/episode).</summary>
    public VideoContentType? VideoTypeAuto { get; set; }

    /// <summary>Final content type.</summary>
    public VideoContentType VideoType { get; set; } = VideoContentType.Unknown;

    /// <summary>Auto-detected IMDb ID.</summary>
    public string? ImdbIdAuto { get; set; }

    /// <summary>Auto-detected poster URL.</summary>
    public string? PosterUrlAuto { get; set; }

    /// <summary>Auto-detected season number (TV series).</summary>
    public int? SeasonNumberAuto { get; set; }

    /// <summary>Final season number (TV series).</summary>
    public int? SeasonNumber { get; set; }

    /// <summary>Auto-detected disc number (TV series).</summary>
    public int? DiscNumberAuto { get; set; }

    /// <summary>Final disc number (TV series).</summary>
    public int? DiscNumber { get; set; }

    /// <summary>User-specified starting episode number.</summary>
    public int? StartingEpisodeNumber { get; set; }

    /// <summary>Whether the title is approved ("nice").</summary>
    public bool HasNiceTitle { get; set; }

    /// <summary>Disc type for UI rendering.</summary>
    public string? DiscType { get; set; }

    /// <summary>Number of tracks/titles.</summary>
    public int? NoOfTitles { get; set; }

    /// <summary>Alias of NoOfTitles for clearer naming in new clients.</summary>
    public int? TitleCount
    {
        get => NoOfTitles;
        set => NoOfTitles = value;
    }

    /// <summary>Poster URL fetched during identification.</summary>
    public string? PosterUrl { get; set; }

    /// <summary>Year fetched during identification.</summary>
    public string? Year { get; set; }

    /// <summary>Whether the tray was ejected.</summary>
    public bool Ejected { get; set; }

    /// <summary>Create a JobUpdate snapshot from a Job entity.</summary>
    public static JobUpdate FromJob(Job job) => new()
    {
        JobId = job.Id,
        Status = job.Status.ToDbString(),
        Stage = job.Stage.HasValue ? job.Stage.Value.ToClientString() : null,
        StageStartTime = job.StageStartTime?.ToString("o"),
        MakeMkvProgress = job.MakeMkvProgress,
        TranscodeProgress = job.TranscodeProgress,
        ProgressMessage = job.ProgressMessage,
        Errors = job.Errors,
        Warnings = job.Warnings,
        StopTime = job.StopTime?.ToString("o"),
        JobLength = job.JobLength,
        Path = job.Path,
        Title = job.Title,
        TitleAuto = job.TitleAuto,
        YearAuto = job.YearAuto,
        VideoTypeAuto = job.VideoTypeAuto,
        VideoType = job.VideoType,
        ImdbIdAuto = job.ImdbIdAuto,
        PosterUrlAuto = job.PosterUrlAuto,
        SeasonNumberAuto = job.SeasonNumberAuto,
        SeasonNumber = job.SeasonNumber,
        DiscNumberAuto = job.DiscNumberAuto,
        DiscNumber = job.DiscNumber,
        StartingEpisodeNumber = job.StartingEpisodeNumber,
        HasNiceTitle = job.HasNiceTitle,
        PosterUrl = job.PosterUrl,
        Year = job.Year,
        DiscType = job.DiscType.ToString(),
        NoOfTitles = job.NoOfTitles,
        Ejected = job.Ejected,
    };
}
