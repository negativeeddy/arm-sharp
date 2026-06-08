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
    public string? Stage { get; set; }
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
    public DiscType DiscType { get; set; } = DiscType.Unknown;
    public string? Label { get; set; }
    public string? Path { get; set; }
    public bool Ejected { get; set; }
    public int? Pid { get; set; }
    public string? PidHash { get; set; }
    public bool IsIso { get; set; }
    public bool ManualStart { get; set; }
    public bool ManualMode { get; set; }
    public bool HasTrack99 { get; set; }
    public string? DiscFingerprint { get; set; }
    public int? MakeMkvProgress { get; set; }
    public int? TranscodeProgress { get; set; }

    public ICollection<Track> Tracks { get; init; } = new List<Track>();
    public ConfigSnapshot? Config { get; set; }
}
