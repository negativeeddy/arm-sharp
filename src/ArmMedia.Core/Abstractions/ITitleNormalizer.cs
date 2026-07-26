namespace ArmMedia.Core.Abstractions;

/// <summary>
/// Normalizes raw disc info titles (e.g., from MakeMKV scan or DiscDb metadata)
/// into a clean query string suitable for TMDB search, along with extracted
/// hints such as season number, disc number, and edition.
/// </summary>
public interface ITitleNormalizer
{
    /// <summary>
    /// Normalizes a raw title string, extracting structured hints and producing
    /// a clean query for metadata lookup.
    /// </summary>
    /// <param name="rawTitle">The raw title from MakeMKV or DiscDb (e.g., "Season 3 Disc 2: Episodes 13-18").</param>
    /// <returns>A <see cref="TitleNormalizationResult"/> with the cleaned query and extracted hints.</returns>
    TitleNormalizationResult Normalize(string rawTitle);
}

/// <summary>
/// Result of title normalization, containing the cleaned query string and any
/// structured hints extracted from the raw title.
/// </summary>
public sealed class TitleNormalizationResult
{
    /// <summary>
    /// The cleaned query string suitable for TMDB search.
    /// Season/disc/edition tokens have been stripped.
    /// </summary>
    public required string Query { get; init; }

    /// <summary>The season number extracted from the title, if any.</summary>
    public int? Season { get; init; }

    /// <summary>The disc number extracted from the title, if any.</summary>
    public int? Disc { get; init; }

    /// <summary>The edition token extracted from the title (e.g., "bluray", "4k"), if any.</summary>
    public string? Edition { get; init; }

    /// <summary>The episode number or range extracted from the title, if any.</summary>
    public string? EpisodeHint { get; init; }
}
