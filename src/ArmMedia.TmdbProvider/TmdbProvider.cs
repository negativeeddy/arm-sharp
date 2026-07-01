using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using ArmMedia.Core.Abstractions;
using ArmMedia.Core.Models;
using Microsoft.Extensions.Logging;

namespace ArmMedia.TmdbProvider;

/// <summary>
/// An <see cref="IEpisodeIdentificationProvider"/> that queries TheMovieDB (TMDB)
/// to identify TV episodes by series title and season number.
/// Returns results with <see cref="Confidence.High"/> when a matching series
/// is found and episodes can be assigned.
/// </summary>
public sealed class TmdbProvider : IEpisodeIdentificationProvider
{
    private readonly ITmdbApiKeySource          _apiKeySource;
    private readonly ILogger<TmdbProvider>      _logger;
    private readonly IHttpClientFactory?        _httpClientFactory;

    private const string BaseUrl = "https://api.themoviedb.org/3";

    /// <summary>Initialises the provider with an API key source, logger, and optional HTTP client factory.</summary>
    public TmdbProvider(
        ITmdbApiKeySource            apiKeySource,
        ILogger<TmdbProvider>        logger,
        IHttpClientFactory?          httpClientFactory = null)
    {
        _apiKeySource       = apiKeySource;
        _logger             = logger;
        _httpClientFactory  = httpClientFactory;
    }

    /// <inheritdoc/>
    public string ProviderName => "Tmdb";

    /// <inheritdoc/>
    public async Task<ProviderResult[]> IdentifyAsync(
        DiscContext       context,
        CancellationToken cancellationToken = default)
    {
        var apiKey = _apiKeySource.GetApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogDebug("[TmdbProvider] No API key configured; skipping TMDB lookup.");
            return [];
        }

        if (string.IsNullOrWhiteSpace(context.SeriesTitle))
        {
            _logger.LogDebug("[TmdbProvider] No series title in context; skipping TMDB lookup.");
            return [];
        }

        // ── Step 1: Search for the TV series ──────────────────────────────────
        int? seriesId = await SearchSeriesAsync(context.SeriesTitle, apiKey, cancellationToken);
        if (seriesId is null)
        {
            _logger.LogInformation(
                "[TmdbProvider] No TMDB series found for '{Title}'.",
                context.SeriesTitle);
            return [];
        }

        _logger.LogInformation(
            "[TmdbProvider] Found TMDB series '{Title}' (ID {SeriesId}).",
            context.SeriesTitle, seriesId);

        // ── Step 2: Get season episodes ───────────────────────────────────────
        var seasonEpisodes = await GetSeasonEpisodesAsync(
            seriesId.Value, context.Season, apiKey, cancellationToken);

        if (seasonEpisodes is null || seasonEpisodes.Count == 0)
        {
            _logger.LogInformation(
                "[TmdbProvider] No episodes found for series {SeriesId}, season {Season}.",
                seriesId, context.Season);
            return [];
        }

        _logger.LogInformation(
            "[TmdbProvider] Loaded {Count} episodes for series {SeriesId}, season {Season}.",
            seasonEpisodes.Count, seriesId, context.Season);

        // ── Step 3: Map tracks to episodes by position ────────────────────────
        var orderedTracks = context.Tracks.OrderBy(t => t.TrackIndex).ToList();
        var results = new List<ProviderResult>();

        var offset = (context.StartingEpisodeNumber ?? 1) - 1; // 0-based index into seasonEpisodes[]
        if (context.StartingEpisodeNumber is not null)
        {
            _logger.LogInformation(
                "[TmdbProvider] Using manual starting episode offset {Offset}.",
                context.StartingEpisodeNumber);
        }

        for (int i = 0; i < orderedTracks.Count; i++)
        {
            var track = orderedTracks[i];

            // Find the matching episode by position (track N → episode offset + N)
            TmdbEpisode? matchingEpisode = null;
            var episodeIndex = offset + i;
            if (episodeIndex < seasonEpisodes.Count)
            {
                matchingEpisode = seasonEpisodes[episodeIndex];
            }

            if (matchingEpisode is not null)
            {
                bool isExtra = matchingEpisode.EpisodeNumber <= 0;

                results.Add(new ProviderResult
                {
                    TrackIndex   = track.TrackIndex,
                    Season       = isExtra ? 0 : context.Season,
                    Episodes     = [matchingEpisode.EpisodeNumber],
                    Title        = matchingEpisode.Name,
                    IsExtra      = isExtra,
                    Confidence   = Confidence.High,
                    ProviderName = ProviderName
                });

                _logger.LogDebug(
                    "[TmdbProvider] Track {TrackIdx} → S{Season}E{Ep} '{Title}'",
                    track.TrackIndex, context.Season,
                    matchingEpisode.EpisodeNumber, matchingEpisode.Name);
            }
            else
            {
                // More tracks than TMDB episodes — leave unidentified for
                // positional fallback or another provider.
                _logger.LogDebug(
                    "[TmdbProvider] No episode mapping for track {TrackIdx} (beyond season episode count).",
                    track.TrackIndex);
            }
        }

        _logger.LogInformation(
            "[TmdbProvider] Mapped {Count}/{Total} tracks from TMDB.",
            results.Count, context.Tracks.Count);

        return results.ToArray();
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private HttpClient CreateClient()
    {
        return _httpClientFactory?.CreateClient("Tmdb")
            ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }

    private async Task<int?> SearchSeriesAsync(
        string title, string apiKey, CancellationToken ct)
    {
        var client = CreateClient();
        var url = $"{BaseUrl}/search/tv" +
                  $"?api_key={apiKey}" +
                  $"&query={Uri.EscapeDataString(title)}";

        TmdbSearchResponse? response;
        try
        {
            response = await client.GetFromJsonAsync<TmdbSearchResponse>(url, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[TmdbProvider] Search failed for '{Title}'.", title);
            return null;
        }

        if (response?.Results is null || response.Results.Count == 0)
            return null;

        // Return the first result's ID
        return response.Results[0].Id;
    }

    private async Task<List<TmdbEpisode>?> GetSeasonEpisodesAsync(
        int seriesId, int season, string apiKey, CancellationToken ct)
    {
        var client = CreateClient();
        var url = $"{BaseUrl}/tv/{seriesId}/season/{season}" +
                  $"?api_key={apiKey}";

        TmdbSeasonResponse? response;
        try
        {
            response = await client.GetFromJsonAsync<TmdbSeasonResponse>(url, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[TmdbProvider] Failed to get season {Season} for series {SeriesId}.",
                season, seriesId);
            return null;
        }

        return response?.Episodes;
    }

    // ── API DTOs ──────────────────────────────────────────────────────────────

    private sealed class TmdbSearchResponse
    {
        [JsonPropertyName("results")]
        public List<TmdbSearchResult>? Results { get; set; }
    }

    private sealed class TmdbSearchResult
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("first_air_date")]
        public string? FirstAirDate { get; set; }
    }

    private sealed class TmdbSeasonResponse
    {
        [JsonPropertyName("episodes")]
        public List<TmdbEpisode>? Episodes { get; set; }
    }

    private sealed class TmdbEpisode
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("episode_number")]
        public int EpisodeNumber { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("runtime")]
        public int? Runtime { get; set; }

        [JsonPropertyName("still_path")]
        public string? StillPath { get; set; }

        [JsonPropertyName("air_date")]
        public string? AirDate { get; set; }
    }
}
