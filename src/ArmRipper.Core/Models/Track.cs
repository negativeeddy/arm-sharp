using System.ComponentModel.DataAnnotations.Schema;

namespace ArmRipper.Core.Models;

public class Track
{
    public int Id { get; init; }
    public int JobId { get; set; }
    public string? TrackNumber { get; set; }

    /// <summary>Pre-parsed track number for efficient numeric access.</summary>
    [NotMapped]
    public int? TrackNumberInt => int.TryParse(TrackNumber, out var n) ? n : null;
    public int? Length { get; set; }
    public string? AspectRatio { get; set; }
    public double? Fps { get; set; }
    public bool MainFeature { get; set; }
    public string? BaseName { get; set; }
    public string? FileName { get; set; }
    public string? OrigFileName { get; set; }
    public string? NewFileName { get; set; }
    public bool Ripped { get; set; }
    public string? Status { get; set; }
    public string? Error { get; set; }
    public string? Source { get; set; }
    public bool Process { get; set; }
    public int? Chapters { get; set; }
    public long? FileSize { get; set; }

    /// <summary>Episode number within the season (null for movies/extras).</summary>
    public int? EpisodeNumber { get; set; }

    /// <summary>Episode title from TheDiscDb (e.g. "Pilot", "Ozymandias").</summary>
    public string? EpisodeTitle { get; set; }

    /// <summary>Content type from TheDiscDb: "movie", "episode", "extra", "trailer", "commentary", etc.</summary>
    public string? ContentType { get; set; }

    /// <summary>Season number for this specific track (may differ from Job.SeasonNumber for multi-season discs).</summary>
    public int? TrackSeasonNumber { get; set; }

    /// <summary>TheDiscDb item slug for this track's content.</summary>
    public string? DiscDbItemSlug { get; set; }

    /// <summary>
    /// MakeMKV segment map for this title (TINFO field 21). A comma-separated
    /// list of cell ranges (e.g. "1,2-3,4"). Used to distinguish titles of
    /// similar length on discs that contain many near-duplicate playlists.
    /// May also be supplied by TheDiscDb when MakeMKV did not report one.
    /// </summary>
    public string? SegmentMap { get; set; }

    public Job Job { get; set; } = null!;
}
