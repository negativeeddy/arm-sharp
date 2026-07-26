using System.Text.Json.Serialization;

namespace ArmMedia.TmdbProvider.Models;

/// <summary>
/// Represents a single episode within a TMDB TV season.
/// Obtained via <c>GET /tv/{id}/season/{n}</c>.
/// </summary>
public sealed class TmdbEpisode
{
    /// <summary>TMDB episode ID.</summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>Episode number within the season (1-based).</summary>
    [JsonPropertyName("episode_number")]
    public int EpisodeNumber { get; set; }

    /// <summary>Season number this episode belongs to.</summary>
    [JsonPropertyName("season_number")]
    public int SeasonNumber { get; set; }

    /// <summary>Episode title.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>Episode overview / synopsis.</summary>
    [JsonPropertyName("overview")]
    public string? Overview { get; set; }

    /// <summary>Runtime in minutes, or <c>null</c> if unknown.</summary>
    [JsonPropertyName("runtime")]
    public int? Runtime { get; set; }

    /// <summary>Air date (ISO 8601, e.g. <c>"2011-05-01"</c>).</summary>
    [JsonPropertyName("air_date")]
    public string? AirDate { get; set; }

    /// <summary>Path to the episode still image on TMDB image servers.</summary>
    [JsonPropertyName("still_path")]
    public string? StillPath { get; set; }

    /// <summary>TMDB vote average (0–10).</summary>
    [JsonPropertyName("vote_average")]
    public double VoteAverage { get; set; }
}
