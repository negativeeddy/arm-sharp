namespace ArmRipper.Core.Models;

/// <summary>
/// Caches TheDiscDb lookup results so re-rips of the same disc don't require
/// a GraphQL API call. Keyed by content hash.
/// </summary>
public class DiscDbMapping
{
    public int Id { get; init; }

    /// <summary>TheDiscDb disc content hash (uppercase hex).</summary>
    public string ContentHash { get; set; } = "";

    /// <summary>TheDiscDb media item slug.</summary>
    public string? MediaSlug { get; set; }

    /// <summary>Title from TheDiscDb (series or movie).</summary>
    public string? MediaTitle { get; set; }

    /// <summary>Year from TheDiscDb.</summary>
    public string? MediaYear { get; set; }

    /// <summary>"movie" or "series".</summary>
    public string? MediaType { get; set; }

    /// <summary>Relative image URL from TheDiscDb (e.g. "Movie/freaky-friday-2003/cover.jpg").</summary>
    public string? ImageUrl { get; set; }

    /// <summary>JSON-serialized track mappings for this disc.</summary>
    public string? TrackMappingsJson { get; set; }

    /// <summary>When this mapping was last successfully used.</summary>
    public DateTime LastUsedAt { get; set; } = DateTime.UtcNow;

    /// <summary>When this mapping was first created.</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
