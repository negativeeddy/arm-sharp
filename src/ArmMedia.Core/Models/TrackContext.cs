namespace ArmMedia.Core.Models;

/// <summary>
/// Per-track information reported by MakeMKV and associated disc scanners.
/// Passed to every provider so they can perform runtime-based matching.
/// </summary>
public sealed class TrackContext
{
    /// <summary>Gets the zero-based physical track index on the disc.</summary>
    public required int TrackIndex { get; init; }

    /// <summary>Gets the playback duration of the track.</summary>
    public required TimeSpan Duration { get; init; }

    /// <summary>Gets the compressed file size in bytes as reported by MakeMKV.</summary>
    public required long SizeBytes { get; init; }

    /// <summary>
    /// Gets the chapter count for this track, if available from the disc TOC.
    /// </summary>
    public int? ChapterCount { get; init; }

    /// <summary>
    /// Gets the DiscDb track identifier, if a prior DiscDb scan populated it.
    /// Providers that call <c>DiscDbMappingService</c> may use this for fast lookup.
    /// </summary>
    public string? DiscDbTrackId { get; init; }

    /// <summary>
    /// Gets raw MakeMKV property bag entries for provider-specific inspection.
    /// Keys use MakeMKV property identifiers (e.g., "DurationString", "CodecId").
    /// </summary>
    public IDictionary<string, string> RawProperties { get; init; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}
