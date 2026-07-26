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
    private readonly ITitleNormalizer?          _titleNormalizer;
    private readonly ILogger<TmdbProvider>      _logger;
    private readonly IHttpClientFactory?        _httpClientFactory;

    private const string BaseUrl = "https://api.themoviedb.org/3";

    /// <summary>Initialises the provider with an API key source, logger, and optional HTTP client factory.</summary>
    public TmdbProvider(
        ITmdbApiKeySource            apiKeySource,
        ILogger<TmdbProvider>        logger,
        IHttpClientFactory?          httpClientFactory = null,
        ITitleNormalizer?            titleNormalizer = null)
    {
        _apiKeySource       = apiKeySource;
        _titleNormalizer    = titleNormalizer;
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

        // ── Step 0: Normalize title for search ────────────────────────────────
        var normalizedTitle = context.SeriesTitle;
        int? seasonOverride = null;
        if (_titleNormalizer is not null)
        {
            var norm = _titleNormalizer.Normalize(context.SeriesTitle);
            _logger.LogDebug(
                "[TmdbProvider] Title normalized: '{Raw}' \u2192 query='{Query}', season={Season}, disc={Disc}, edition='{Edition}'.",
                context.SeriesTitle, norm.Query, norm.Season, norm.Disc, norm.Edition);

            // Use the cleaned query for search (if non-empty), else fall back to raw title
            if (!string.IsNullOrWhiteSpace(norm.Query))
                normalizedTitle = norm.Query;

            // If the title contained an explicit season hint and the context season is the default,
            // prefer the extracted season.
            if (norm.Season is int s && context.Season <= 1)
                seasonOverride = s;
        }

        // ── Step 1: Search for the TV series ──────────────────────────────────
        int? seriesId = await SearchSeriesAsync(normalizedTitle, apiKey, cancellationToken);
        if (seriesId is null)
        {
            // If normalization changed the query, retry with the original title
            if (normalizedTitle != context.SeriesTitle)
            {
                _logger.LogDebug(
                    "[TmdbProvider] Normalized search returned no results; retrying with original title '{Title}'.",
                    context.SeriesTitle);
                seriesId = await SearchSeriesAsync(context.SeriesTitle, apiKey, cancellationToken);
            }

            if (seriesId is null)
            {
                _logger.LogInformation(
                    "[TmdbProvider] No TMDB series found for '{Title}'.",
                    context.SeriesTitle);
                return [];
            }
        }

        // Apply season override from title normalization if context season was not explicit
        var effectiveSeason = seasonOverride ?? context.Season;

        _logger.LogInformation(
            "[TmdbProvider] Found TMDB series '{Title}' (ID {SeriesId}).",
            context.SeriesTitle, seriesId);

        // ── Step 2: Get season episodes ───────────────────────────────────────
        var seasonEpisodes = await GetSeasonEpisodesAsync(
            seriesId.Value, effectiveSeason, apiKey, cancellationToken);

        if (seasonEpisodes is null || seasonEpisodes.Count == 0)
        {
            _logger.LogInformation(
                "[TmdbProvider] No episodes found for series {SeriesId}, season {Season}.",
                seriesId, effectiveSeason);
            return [];
        }

        _logger.LogInformation(
            "[TmdbProvider] Loaded {Count} episodes for series {SeriesId}, season {Season}.",
            seasonEpisodes.Count, seriesId, effectiveSeason);

        // ── Step 3: Map tracks to episodes by position ────────────────────────
        var orderedTracks = context.Tracks.OrderBy(t => t.TrackIndex).ToList();
        var results = new List<ProviderResult>();

        var offset = (context.StartingEpisodeNumber ?? 1) - 1;

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
                    Season       = isExtra ? 0 : effectiveSeason,
                    Episodes     = [matchingEpisode.EpisodeNumber],
                    Title        = matchingEpisode.Name,
                    IsExtra      = isExtra,
                    Confidence   = Confidence.High,
                    ProviderName = ProviderName
                });

                _logger.LogDebug(
                    "[TmdbProvider] Track {TrackIdx} → S{Season}E{Ep} '{Title}'",
                    track.TrackIndex, effectiveSeason,
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
