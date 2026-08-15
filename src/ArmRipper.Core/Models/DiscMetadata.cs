namespace ArmRipper.Core.Models;

public class DiscMetadata
{
    public int Id { get; init; }
    public string Fingerprint { get; set; } = "";
    public string VolumeLabel { get; set; } = "";
    public long SectorCount { get; set; }
    public string DiscType { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime LastUsedAt { get; set; }

    public ICollection<DiscTrack> Tracks { get; init; } = new List<DiscTrack>();

    /// <summary>
    /// User-selected main feature track number for this disc fingerprint. When
    /// set, future rips of the same disc use this track as the main feature
    /// instead of the automatic selection.
    /// </summary>
    public string? MainFeatureTrackNumber { get; set; }
}
