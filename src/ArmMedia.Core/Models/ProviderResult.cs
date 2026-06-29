namespace ArmMedia.Core.Models;

/// <summary>
/// Immutable identification result returned by a single provider for a single disc track.
/// The orchestrator merges results from all providers, selecting the highest-confidence
/// result per track.
/// </summary>
public sealed record ProviderResult
{
    /// <summary>Gets the zero-based physical track index this result applies to.</summary>
    public required int TrackIndex { get; init; }

    /// <summary>Gets the season number (1-based; 0 = specials/extras).</summary>
    public required int Season { get; init; }

    /// <summary>
    /// Gets the episode number(s) assigned to this track.
    /// A single-element array indicates a normal episode.
    /// A multi-element array indicates a multi-part episode (e.g., <c>[3, 4]</c>).
    /// </summary>
    public required int[] Episodes { get; init; }

    /// <summary>Gets the episode title as returned by this provider, or <c>null</c> if unknown.</summary>
    public string? Title { get; init; }

    /// <summary>
    /// Gets a value indicating whether this track is classified as an extra or bonus feature
    /// rather than a main-series episode.
    /// </summary>
    public bool IsExtra { get; init; }

    /// <summary>Gets the confidence level of this identification result.</summary>
    public Confidence Confidence { get; init; }

    /// <summary>Gets the name of the provider that produced this result.</summary>
    public required string ProviderName { get; init; }
}

/// <summary>
/// Confidence levels used by providers and the orchestrator's merge strategy.
/// Higher values take precedence when two providers disagree on the same track.
/// </summary>
public enum Confidence
{
    /// <summary>Positional guess; no real metadata was available.</summary>
    Low = 0,

    /// <summary>Runtime heuristic match (within configured tolerance).</summary>
    Medium = 1,

    /// <summary>Strong metadata + runtime agreement.</summary>
    High = 2,

    /// <summary>Exact disc ID match from DiscDb; pipeline short-circuits.</summary>
    Definitive = 3
}
