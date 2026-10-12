namespace ArmRipper.Core.Models;

public class DiscTrack
{
    public int Id { get; init; }
    public int DiscMetadataId { get; set; }
    public string TrackNumber { get; set; } = "";
    public string? FileName { get; set; }
    public int? Length { get; set; }
    public int? Chapters { get; set; }
    public long? FileSize { get; set; }
    public string? AspectRatio { get; set; }
    public double? Fps { get; set; }
    public string? Resolution { get; set; }

    /// <summary>MakeMKV segment map for this title (TINFO field 21).</summary>
    public string? SegmentMap { get; set; }

    public DiscMetadata DiscMetadata { get; set; } = null!;
    public ICollection<DiscTrackStream> Streams { get; init; } = new List<DiscTrackStream>();
}
