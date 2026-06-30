namespace ArmMedia.Core.Models;

/// <summary>
/// The final merged output of the <see cref="Abstractions.IEpisodeIdentificationOrchestrator"/>.
/// Contains one <see cref="MappedTrack"/> for every physical track on the disc.
/// Consumed by the naming and linting subsystems.
/// </summary>
public sealed class EpisodeMap
{
    /// <summary>Gets the canonical series title.</summary>
    public required string SeriesTitle { get; init; }

    /// <summary>Gets the season number (1-based; 0 = specials/extras disc).</summary>
    public required int Season { get; init; }

    /// <summary>Gets the ordered list of mapped tracks, one per physical disc track.</summary>
    public required IReadOnlyList<MappedTrack> Tracks { get; init; }

    /// <summary>Gets the UTC timestamp at which this map was generated.</summary>
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Represents the identification result for a single physical disc track after
/// the orchestrator has merged all provider results and applied post-processing.
/// </summary>
public sealed class MappedTrack
{
    /// <summary>Gets the zero-based physical track index on the disc.</summary>
    public required int TrackIndex { get; init; }

    /// <summary>Gets the season number (1-based; 0 = specials/extras).</summary>
    public required int Season { get; init; }

    /// <summary>
    /// Gets the episode number(s) assigned to this track.
    /// Multi-element arrays indicate merged multi-part episodes (e.g., <c>[3, 4]</c>).
    /// </summary>
    public required int[] Episodes { get; init; }

    /// <summary>Gets the episode title, or <c>null</c> if no provider could supply one.</summary>
    public string? Title { get; init; }

    /// <summary>
    /// Gets the playback duration of the track, if available from the original
    /// <see cref="TrackContext"/>. Used by lint rules such as TV003 and TV005.
    /// </summary>
    public TimeSpan? Duration { get; init; }

    /// <summary>
    /// Gets the compressed file size in bytes, if available from the original
    /// <see cref="TrackContext"/>.
    /// </summary>
    public long? SizeBytes { get; init; }

    /// <summary>
    /// Gets a value indicating whether this track is an extra/bonus feature (Season 0 or short duration).
    /// </summary>
    public bool IsExtra { get; init; }

    /// <summary>
    /// Gets a value indicating whether this track spans multiple consecutive episodes.
    /// Derived from <see cref="Episodes"/>.
    /// </summary>
    public bool IsMultiPart => Episodes.Length > 1;

    /// <summary>Gets the name of the provider that supplied the winning identification.</summary>
    public required string WinningProvider { get; init; }

    /// <summary>Gets the confidence level of the winning provider's result.</summary>
    public required Confidence Confidence { get; init; }
}
