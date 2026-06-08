namespace ArmRipper.Core.Models;

public class DiscTrackStream
{
    public int Id { get; init; }
    public int DiscTrackId { get; set; }
    public int StreamIndex { get; set; }
    public string StreamType { get; set; } = "";
    public string? LanguageCode { get; set; }
    public string? Codec { get; set; }
    public int? ChannelCount { get; set; }
    public bool Forced { get; set; }

    public DiscTrack DiscTrack { get; set; } = null!;
}
