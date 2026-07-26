using System.Text.Json.Serialization;

namespace ArmMedia.TmdbProvider.Models;

/// <summary>
/// Represents the top-level details of a TV series from TMDB.
/// Obtained via <c>GET /tv/{id}</c>.
/// </summary>
public sealed class TmdbTvDetails
{
    /// <summary>TMDB series ID.</summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>Series name.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>Original name in the production language.</summary>
    [JsonPropertyName("original_name")]
    public string? OriginalName { get; set; }

    /// <summary>Overview / synopsis.</summary>
    [JsonPropertyName("overview")]
    public string? Overview { get; set; }

    /// <summary>First air date (ISO 8601, e.g. <c>"2011-04-17"</c>).</summary>
    [JsonPropertyName("first_air_date")]
    public string? FirstAirDate { get; set; }

    /// <summary>Total number of seasons (including specials).</summary>
    [JsonPropertyName("number_of_seasons")]
    public int NumberOfSeasons { get; set; }

    /// <summary>Total number of episodes across all seasons.</summary>
    [JsonPropertyName("number_of_episodes")]
    public int NumberOfEpisodes { get; set; }

    /// <summary>TMDB vote average (0–10).</summary>
    [JsonPropertyName("vote_average")]
    public double VoteAverage { get; set; }

    /// <summary>List of seasons (including specials at season 0).</summary>
    [JsonPropertyName("seasons")]
    public List<TmdbSeasonSummary>? Seasons { get; set; }
}

/// <summary>
/// Summary of a single season within a TV series.
/// </summary>
public sealed class TmdbSeasonSummary
{
    /// <summary>Season number (0 = specials).</summary>
    [JsonPropertyName("season_number")]
    public int SeasonNumber { get; set; }

    /// <summary>Episode count for this season.</summary>
    [JsonPropertyName("episode_count")]
    public int EpisodeCount { get; set; }

    /// <summary>Season name (e.g. <c>"Season 1"</c>).</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>Air date of the first episode.</summary>
    [JsonPropertyName("air_date")]
    public string? AirDate { get; set; }
}
