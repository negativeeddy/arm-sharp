namespace ArmMedia.Core.Models;

/// <summary>
/// Slim, provider-friendly representation of a DiscDb lookup result.
/// This is a projection of <c>ArmRipper.Core.Rip.DiscDbMediaResult</c>
/// and keeps the provider layer decoupled from the host's DiscDb implementation.
/// </summary>
public sealed class DiscDbLookupResult
{
    /// <summary>Gets the media title (series or movie name).</summary>
    public required string Title { get; init; }

    /// <summary>Gets the release year, if available.</summary>
    public string? Year { get; init; }

    /// <summary>Gets the per-disc track-level entries.</summary>
    public IReadOnlyList<DiscDbLookupTrack> Tracks { get; init; } = [];
}

/// <summary>
/// A single track entry from a DiscDb lookup, mapped to a specific
/// physical track index on the disc.
/// </summary>
public sealed class DiscDbLookupTrack
{
    /// <summary>Gets the zero-based physical track index on the disc.</summary>
    public required int TrackIndex { get; init; }

    /// <summary>
    /// Gets the episode title as provided by the DiscDb record, or <c>null</c>.
    /// </summary>
    public string? Title { get; init; }

    /// <summary>
    /// Gets the season number (1-based; 0 = specials/extras).
    /// </summary>
    public int? Season { get; init; }

    /// <summary>
    /// Gets the episode number within the season.
    /// </summary>
    public int? Episode { get; init; }

    /// <summary>
    /// Gets the content type label (e.g., "main", "extra", "trailer", "deleted_scene").
    /// </summary>
    public string? ContentType { get; init; }
}
