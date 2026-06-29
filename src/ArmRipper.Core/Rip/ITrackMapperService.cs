using ArmRipper.Core.Models;

namespace ArmRipper.Core.Rip;

/// <summary>Matches TheDiscDb track metadata to MakeMKV-identified tracks.</summary>
public interface ITrackMapperService
{
    /// <summary>
    /// Match TheDiscDb titles to MakeMKV-identified tracks.
    /// Populates Track.EpisodeNumber, Track.EpisodeTitle, Track.ContentType, etc.
    /// Returns match confidence score (0.0–1.0).
    /// </summary>
    Task<double> MapTracksAsync(Job job, DiscDbMediaResult? discDbResult, CancellationToken ct = default);
}
