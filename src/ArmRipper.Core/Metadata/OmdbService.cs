using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace ArmRipper.Core.Metadata;

public sealed class OmdbService(ILoggerFactory loggerFactory, HttpClient httpClient)
{
    private readonly ILogger logger = loggerFactory.CreateLogger("OmdbService");
    public async Task<OmdbSearchResult?> SearchAsync(string apiKey, string title, string? year = null, string? plot = "short", bool exact = false, CancellationToken ct = default)
    {
        if (exact)
            return await SearchExactAsync(apiKey, title, year, plot, ct);

        var url = $"https://www.omdbapi.com/?s={Uri.EscapeDataString(title)}&plot={plot}&r=json&apikey={apiKey}";
        if (!string.IsNullOrEmpty(year))
            url = $"https://www.omdbapi.com/?s={Uri.EscapeDataString(title)}&y={Uri.EscapeDataString(year)}&plot={plot}&r=json&apikey={apiKey}";

        try
        {
            var result = await httpClient.GetFromJsonAsync<OmdbSearchResult>(url, ct);
            if (result is not null && result.Response == "True")
                return result;

            // Return the result even on error so callers can read the Error property
            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "OMDB API call failed for title={Title}", title);
            return null;
        }
    }

    /// <summary>
    /// Looks up a single title using the OMDB exact-title endpoint (t=).
    /// Returns the result wrapped as an <see cref="OmdbSearchResult"/> with a single item,
    /// or null if the lookup fails.
    /// </summary>
    private async Task<OmdbSearchResult?> SearchExactAsync(string apiKey, string title, string? year, string? plot, CancellationToken ct)
    {
        var exactUrl = $"https://www.omdbapi.com/?t={Uri.EscapeDataString(title)}&plot={plot}&r=json&apikey={apiKey}";
        if (!string.IsNullOrEmpty(year))
            exactUrl += $"&y={Uri.EscapeDataString(year)}";

        try
        {
            var exact = await httpClient.GetFromJsonAsync<OmdbTitleResult>(exactUrl, ct);
            if (exact is not null && exact.Response == "True")
            {
                return new OmdbSearchResult
                {
                    Response = "True",
                    Search =
                    [
                        new OmdbSearchItem
                        {
                            Title = exact.Title,
                            Year = exact.Year,
                            ImdbID = exact.ImdbID,
                            Type = exact.Type,
                            Poster = exact.Poster
                        }
                    ]
                };
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "OMDB exact title lookup failed for title={Title}", title);
        }

        return null;
    }

    public async Task<OmdbTitleResult?> LookupByImdbAsync(string imdbId, string apiKey, string? plot = "short", CancellationToken ct = default)
    {
        var url = $"https://www.omdbapi.com/?i={imdbId}&plot={plot}&r=json&apikey={apiKey}";
        try
        {
            return await httpClient.GetFromJsonAsync<OmdbTitleResult>(url, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "OMDB lookup failed for imdbId={ImdbId}", imdbId);
            return null;
        }
    }

    public async Task<(string? posterUrl, string? imdbId)> GetPosterAsync(string apiKey, string? title = null, string? year = null, string? imdbId = null, string? plot = "short", CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(imdbId))
        {
            var result = await LookupByImdbAsync(imdbId, apiKey, plot, ct);
            if (result is not null && result.Response == "True")
                return (result.Poster, result.ImdbID);
        }

        if (!string.IsNullOrEmpty(title))
        {
            var searchResult = await SearchAsync(apiKey, title, year, plot, ct: ct);
            if (searchResult?.Search is { Count: > 0 })
            {
                var first = searchResult.Search[0];
                return (first.Poster, first.ImdbID);
            }

            // Try exact title lookup
            var exactUrl = $"https://www.omdbapi.com/?t={Uri.EscapeDataString(title)}&y={Uri.EscapeDataString(year ?? "")}&plot={plot}&r=json&apikey={apiKey}";
            try
            {
                var exact = await httpClient.GetFromJsonAsync<OmdbTitleResult>(exactUrl, ct);
                if (exact is not null && exact.Response == "True")
                    return (exact.Poster, exact.ImdbID);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "OMDB exact lookup failed for title={Title}", title);
            }
        }

        return (null, null);
    }
}

public class OmdbSearchResult
{
    [JsonPropertyName("Search")]
    public List<OmdbSearchItem>? Search { get; set; }

    [JsonPropertyName("Response")]
    public string? Response { get; set; }

    [JsonPropertyName("Error")]
    public string? Error { get; set; }

    [JsonPropertyName("totalResults")]
    public string? TotalResults { get; set; }
}

public class OmdbSearchItem
{
    [JsonPropertyName("Title")]
    public string? Title { get; set; }

    [JsonPropertyName("Year")]
    public string? Year { get; set; }

    [JsonPropertyName("imdbID")]
    public string? ImdbID { get; set; }

    [JsonPropertyName("Type")]
    public string? Type { get; set; }

    [JsonPropertyName("Poster")]
    public string? Poster { get; set; }
}

public class OmdbTitleResult
{
    [JsonPropertyName("Title")]
    public string? Title { get; set; }

    [JsonPropertyName("Year")]
    public string? Year { get; set; }

    [JsonPropertyName("Rated")]
    public string? Rated { get; set; }

    [JsonPropertyName("Released")]
    public string? Released { get; set; }

    [JsonPropertyName("Runtime")]
    public string? Runtime { get; set; }

    [JsonPropertyName("Genre")]
    public string? Genre { get; set; }

    [JsonPropertyName("Director")]
    public string? Director { get; set; }

    [JsonPropertyName("Writer")]
    public string? Writer { get; set; }

    [JsonPropertyName("Actors")]
    public string? Actors { get; set; }

    [JsonPropertyName("Plot")]
    public string? Plot { get; set; }

    [JsonPropertyName("Language")]
    public string? Language { get; set; }

    [JsonPropertyName("Country")]
    public string? Country { get; set; }

    [JsonPropertyName("Awards")]
    public string? Awards { get; set; }

    [JsonPropertyName("Poster")]
    public string? Poster { get; set; }

    [JsonPropertyName("Ratings")]
    public List<OmdbRating>? Ratings { get; set; }

    [JsonPropertyName("Metascore")]
    public string? Metascore { get; set; }

    [JsonPropertyName("imdbRating")]
    public string? ImdbRating { get; set; }

    [JsonPropertyName("imdbVotes")]
    public string? ImdbVotes { get; set; }

    [JsonPropertyName("imdbID")]
    public string? ImdbID { get; set; }

    [JsonPropertyName("Type")]
    public string? Type { get; set; }

    [JsonPropertyName("DVD")]
    public string? Dvd { get; set; }

    [JsonPropertyName("BoxOffice")]
    public string? BoxOffice { get; set; }

    [JsonPropertyName("Production")]
    public string? Production { get; set; }

    [JsonPropertyName("Website")]
    public string? Website { get; set; }

    [JsonPropertyName("Response")]
    public string? Response { get; set; }

    [JsonPropertyName("Error")]
    public string? Error { get; set; }
}

public class OmdbRating
{
    [JsonPropertyName("Source")]
    public string? Source { get; set; }

    [JsonPropertyName("Value")]
    public string? Value { get; set; }
}
