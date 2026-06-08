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
}
