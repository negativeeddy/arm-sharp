namespace ArmMedia.Core.Models;

/// <summary>
/// Contextual information about a disc gathered prior to ripping.
/// Passed to every <see cref="Abstractions.IEpisodeIdentificationProvider"/> in the pipeline.
/// </summary>
public sealed class DiscContext
{
    /// <summary>
    /// Gets the disc identifier (e.g., Blu-ray barcode, MakeMKV disc hash, or user-supplied label).
    /// Used by the DiscDbProvider for an exact lookup.
    /// </summary>
    public required string DiscId { get; init; }

    /// <summary>
    /// Gets the series title as known to the user or resolved from a prior metadata lookup.
    /// </summary>
    public required string SeriesTitle { get; init; }

    /// <summary>
    /// Gets the expected season number (1-based). Season 0 is used for specials/extras.
    /// </summary>
    public required int Season { get; init; }

    /// <summary>
    /// Gets the ordered list of physical tracks present on the disc.
    /// </summary>
    public required IReadOnlyList<TrackContext> Tracks { get; init; }

    /// <summary>
    /// Gets an optional hint passed to the DiscDb provider (e.g., a manually supplied DiscDb record ID).
    /// </summary>
    public string? DiscDbHint { get; init; }

    /// <summary>
    /// Gets the 1-based disc number within the season, parsed from the physical disc label
    /// (e.g., <c>_D2</c> → 2). Defaults to 1 when no disc suffix is found.
    /// Providers use this to offset into the full season episode list for multi-disc sets.
    /// </summary>
    public int DiscNumber { get; init; } = 1;

    /// <summary>
    /// Gets the index of the track currently being prepared for transcoding.
    /// Used by the ArmSharp glue layer to select the correct <see cref="MappedTrack"/>.
    /// </summary>
    public int? CurrentTrackIndex { get; init; }

    /// <summary>
    /// Gets an optional starting episode number override for sequential/positional
    /// providers (Omdb, Tmdb, Tvdb, PositionalFallback). When set, the first
    /// track will be assigned this episode number instead of 1.
    /// Providers that know the actual disc layout (DvdCompare, DiscDb) ignore
    /// this value and use their own numbering.
    /// </summary>
    public int? StartingEpisodeNumber { get; init; }
}
