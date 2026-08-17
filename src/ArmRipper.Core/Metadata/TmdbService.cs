using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace ArmRipper.Core.Metadata;

public sealed partial class TmdbService(ILogger<TmdbService> logger, HttpClient httpClient)
{
    private const string PosterBase = "https://image.tmdb.org/t/p/original";

    public async Task<TmdbProcessedResult?> SearchMovieAsync(string apiKey, string query, string? year = null, CancellationToken ct = default)
    {
        var url = string.IsNullOrEmpty(year)
            ? $"https://api.themoviedb.org/3/search/movie?api_key={apiKey}&query={Uri.EscapeDataString(query)}"
            : $"https://api.themoviedb.org/3/search/movie?api_key={apiKey}&query={Uri.EscapeDataString(query)}&year={year}";

        try
        {
            var response = await httpClient.GetFromJsonAsync<TmdbSearchResults>(url, ct);
            if (response?.Results is { Count: > 0 })
                return ProcessResults(response.Results, "movie");

            // Search TV
            var tvUrl = $"https://api.themoviedb.org/3/search/tv?api_key={apiKey}&query={Uri.EscapeDataString(query)}";
            var tvResponse = await httpClient.GetFromJsonAsync<TmdbSearchResults>(tvUrl, ct);
            if (tvResponse?.Results is { Count: > 0 })
                return ProcessResults(tvResponse.Results, "series");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "TMDB search failed for query={Query}", query);
        }

        return null;
    }

    public async Task<TmdbProcessedResult?> FindByImdbAsync(string imdbId, string apiKey, CancellationToken ct = default)
    {
        var url = $"https://api.themoviedb.org/3/find/{imdbId}?api_key={apiKey}&external_source=imdb_id";
        try
        {
            var response = await httpClient.GetFromJsonAsync<TmdbFindResult>(url, ct);
            if (response is null) return null;

            if (response.MovieResults is { Count: > 0 })
                return ProcessSingle(response.MovieResults[0], "movie", imdbId);

            if (response.TvResults is { Count: > 0 })
                return ProcessSingle(response.TvResults[0], "series", imdbId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "TMDB find failed for imdbId={ImdbId}", imdbId);
        }

        return null;
    }

    public async Task<string?> GetImdbIdAsync(string apiKey, int tmdbId, bool isTv = false, CancellationToken ct = default)
    {
        try
        {
            if (isTv)
            {
                var url = $"https://api.themoviedb.org/3/tv/{tmdbId}/external_ids?api_key={apiKey}";
                var result = await httpClient.GetFromJsonAsync<TmdbExternalIds>(url, ct);
                return result?.ImdbId;
            }

            var movieUrl = $"https://api.themoviedb.org/3/movie/{tmdbId}?api_key={apiKey}&append_to_response=external_ids";
            var movieResult = await httpClient.GetFromJsonAsync<TmdbMovieWithExternal>(movieUrl, ct);
            return movieResult?.ExternalIds?.ImdbId;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get IMDB ID for TMDB ID {TmdbId}", tmdbId);
            return null;
        }
    }

    public async Task<(string? posterUrl, string? imdbId)> GetPosterAsync(string apiKey, string query, string? year = null, CancellationToken ct = default)
    {
        var result = await SearchMovieAsync(apiKey, query, year, ct);
        if (result is not null)
            return (result.PosterUrl, result.ImdbId);

        return (null, null);
    }

    private static TmdbProcessedResult? ProcessResults(List<TmdbResultItem> results, string mediaType)
    {
        foreach (var item in results)
        {
            if (item.PosterPath is null)
                continue;

            var releaseDate = item.ReleaseDate ?? item.FirstAirDate ?? "";
            var year = releaseDate.Length >= 4 ? releaseDate[..4] : "";
            var title = item.Title ?? item.Name ?? "Unknown";

            return new TmdbProcessedResult
            {
                Title = title,
                Year = year,
                PosterUrl = $"{PosterBase}{item.PosterPath}",
                BackgroundUrl = item.BackdropPath is not null ? $"{PosterBase}{item.BackdropPath}" : null,
                Plot = item.Overview,
                Type = mediaType,
                ImdbId = null,
                TmdbId = item.Id
            };
        }

        return null;
    }

    private static TmdbProcessedResult? ProcessSingle(TmdbResultItem item, string mediaType, string imdbId)
    {
        if (item.PosterPath is null)
            return null;

        var releaseDate = item.ReleaseDate ?? item.FirstAirDate ?? "";
        var year = releaseDate.Length >= 4 ? releaseDate[..4] : "";
        var title = item.Title ?? item.Name ?? "Unknown";

        return new TmdbProcessedResult
        {
            Title = title,
            Year = year,
            PosterUrl = $"{PosterBase}{item.PosterPath}",
            BackgroundUrl = item.BackdropPath is not null ? $"{PosterBase}{item.BackdropPath}" : null,
            Plot = item.Overview,
            Type = mediaType,
            ImdbId = imdbId,
            TmdbId = item.Id
        };
    }
}

public class TmdbProcessedResult
{
    public string? Title { get; set; }
    public string? Year { get; set; }
    public string? PosterUrl { get; set; }
    public string? BackgroundUrl { get; set; }
    public string? Plot { get; set; }
    public string? Type { get; set; }
    public string? ImdbId { get; set; }
    public int? TmdbId { get; set; }
}

public class TmdbSearchResults
{
    [JsonPropertyName("page")]
    public int Page { get; set; }

    [JsonPropertyName("results")]
    public List<TmdbResultItem>? Results { get; set; }

    [JsonPropertyName("total_results")]
    public int TotalResults { get; set; }

    [JsonPropertyName("total_pages")]
    public int TotalPages { get; set; }
}

public class TmdbResultItem
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("overview")]
    public string? Overview { get; set; }

    [JsonPropertyName("release_date")]
    public string? ReleaseDate { get; set; }

    [JsonPropertyName("first_air_date")]
    public string? FirstAirDate { get; set; }

    [JsonPropertyName("poster_path")]
    public string? PosterPath { get; set; }

    [JsonPropertyName("backdrop_path")]
    public string? BackdropPath { get; set; }

    [JsonPropertyName("vote_average")]
    public double VoteAverage { get; set; }

    [JsonPropertyName("vote_count")]
    public int VoteCount { get; set; }

    [JsonPropertyName("media_type")]
    public string? MediaType { get; set; }
}

public class TmdbFindResult
{
    [JsonPropertyName("movie_results")]
    public List<TmdbResultItem>? MovieResults { get; set; }

    [JsonPropertyName("tv_results")]
    public List<TmdbResultItem>? TvResults { get; set; }
}

public class TmdbExternalIds
{
    [JsonPropertyName("imdb_id")]
    public string? ImdbId { get; set; }
}

public class TmdbMovieWithExternal
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("imdb_id")]
    public string? ImdbId { get; set; }

    [JsonPropertyName("external_ids")]
    public TmdbExternalIds? ExternalIds { get; set; }
}
