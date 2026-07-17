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

    public DiscMetadata DiscMetadata { get; set; } = null!;
    public ICollection<DiscTrackStream> Streams { get; init; } = new List<DiscTrackStream>();
}
