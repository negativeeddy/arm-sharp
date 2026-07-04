using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading;
using ArmMedia.Core.Abstractions;
using ArmMedia.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArmMedia.TvdbProvider;

/// <summary>
/// An <see cref="IEpisodeIdentificationProvider"/> that queries TheTVDB (TVDB)
/// to identify TV episodes by series title and season number using DVD episode
/// ordering, which matches the physical track layout on optical discs.
/// Returns results with <see cref="Confidence.High"/>.
/// </summary>
[ArmMedia.Core.DiagnosticName(DiagnosticCategory)]
public sealed class TvdbProvider : IEpisodeIdentificationProvider
{
    private const string DiagnosticCategory = "TvdbProvider";
    private readonly ITvdbApiKeySource          _apiKeySource;
    private readonly TvdbProviderOptions        _options;
    private readonly ILogger                    _logger;
    private readonly IHttpClientFactory?        _httpClientFactory;

    // Token is cached in-memory with a simple expiry (TVDB tokens last ~24h).
    private static string? _cachedToken;
    private static DateTime _tokenExpiryUtc = DateTime.MinValue;

    /// <summary>Initialises the provider with an API key source, options, and logger.</summary>
    public TvdbProvider(
        ITvdbApiKeySource            apiKeySource,
        IOptions<TvdbProviderOptions> options,
        ILoggerFactory               loggerFactory,
        IHttpClientFactory?          httpClientFactory = null)
    {
        _apiKeySource       = apiKeySource;
        _options            = options.Value;
        _logger             = loggerFactory.CreateLogger(DiagnosticCategory);
        _httpClientFactory  = httpClientFactory;
    }

    /// <inheritdoc/>
    public string ProviderName => "Tvdb";

    /// <inheritdoc/>
    public async Task<ProviderResult[]> IdentifyAsync(
        DiscContext       context,
        CancellationToken cancellationToken = default)
    {
        var apiKey = _apiKeySource.GetApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogDebug("[TvdbProvider] No API key configured; skipping TVDB lookup.");
            return [];
        }

        if (string.IsNullOrWhiteSpace(context.SeriesTitle))
        {
            _logger.LogDebug("[TvdbProvider] No series title in context; skipping TVDB lookup.");
            return [];
        }

        // ── Step 0: Authenticate ─────────────────────────────────────────────
        var token = await GetTokenAsync(apiKey, cancellationToken);
        if (token is null)
        {
            _logger.LogWarning("[TvdbProvider] Authentication failed; skipping.");
            return [];
        }

        // ── Step 1: Search for the TV series ──────────────────────────────────
        int? seriesId = await SearchSeriesAsync(
            context.SeriesTitle, token, cancellationToken);
        if (seriesId is null)
        {
            _logger.LogInformation(
                "[TvdbProvider] No TVDB series found for '{Title}'.",
                context.SeriesTitle);
            return [];
        }

        _logger.LogInformation(
            "[TvdbProvider] Found TVDB series '{Title}' (ID {SeriesId}).",
            context.SeriesTitle, seriesId);

        // ── Step 2: Get DVD-order episodes for the season ─────────────────────
        var seasonEpisodes = await GetDvdEpisodesAsync(
            seriesId.Value, context.Season, token, cancellationToken);

        if (seasonEpisodes is null || seasonEpisodes.Count == 0)
        {
            // Fall back to default (aired) order if DVD order is empty
            _logger.LogInformation(
                "[TvdbProvider] No DVD-order episodes for series {SeriesId} S{Season}; trying default order.",
                seriesId, context.Season);
            seasonEpisodes = await GetDefaultEpisodesAsync(
                seriesId.Value, context.Season, token, cancellationToken);
        }

        if (seasonEpisodes is null || seasonEpisodes.Count == 0)
        {
            _logger.LogInformation(
                "[TvdbProvider] No episodes found for series {SeriesId}, season {Season}.",
                seriesId, context.Season);
            return [];
        }

        _logger.LogInformation(
            "[TvdbProvider] Loaded {Count} episodes (DVD order) for series {SeriesId}, season {Season}.",
            seasonEpisodes.Count, seriesId, context.Season);

        // ── Step 3: Map tracks to episodes by position ────────────────────────
        var orderedTracks = context.Tracks.OrderBy(t => t.TrackIndex).ToList();
        var results = new List<ProviderResult>();

        var offset = (context.StartingEpisodeNumber ?? 1) - 1; // 0-based index into seasonEpisodes[]
        if (context.StartingEpisodeNumber is not null)
        {
            _logger.LogInformation(
                "[TvdbProvider] Using manual starting episode offset {Offset}.",
                context.StartingEpisodeNumber);
        }

        for (int i = 0; i < orderedTracks.Count; i++)
        {
            var track = orderedTracks[i];

            TvdbEpisode? matchingEpisode = null;
            var episodeIndex = offset + i;
            if (episodeIndex < seasonEpisodes.Count)
                matchingEpisode = seasonEpisodes[episodeIndex];

            if (matchingEpisode is not null)
            {
                bool isExtra = matchingEpisode.Number <= 0;

                results.Add(new ProviderResult
                {
                    TrackIndex   = track.TrackIndex,
                    Season       = isExtra ? 0 : context.Season,
                    Episodes     = [matchingEpisode.Number],
                    Title        = matchingEpisode.Name,
                    IsExtra      = isExtra,
                    Confidence   = Confidence.High,
                    ProviderName = ProviderName
                });

                _logger.LogDebug(
                    "[TvdbProvider] Track {TrackIdx} → S{Season}E{Ep} '{Title}'",
                    track.TrackIndex, context.Season,
                    matchingEpisode.Number, matchingEpisode.Name);
            }
            else
            {
                _logger.LogDebug(
                    "[TvdbProvider] No episode mapping for track {TrackIdx} (beyond season episode count).",
                    track.TrackIndex);
            }
        }

        _logger.LogInformation(
            "[TvdbProvider] Mapped {Count}/{Total} tracks from TVDB.",
            results.Count, context.Tracks.Count);

        return results.ToArray();
    }

    // ── Auth ─────────────────────────────────────────────────────────────────

    private async Task<string?> GetTokenAsync(string apiKey, CancellationToken ct)
    {
        if (_cachedToken is not null && DateTime.UtcNow < _tokenExpiryUtc)
            return _cachedToken;

        var client = CreateClient();
        var body = new { apikey = apiKey };

        try
        {
            var response = await client.PostAsJsonAsync(
                $"{_options.ApiBaseUrl}/login", body, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content
                .ReadFromJsonAsync<TvdbLoginResponse>(cancellationToken: ct);

            if (result?.Data?.Token is not null)
            {
                _cachedToken = result.Data.Token;
                _tokenExpiryUtc = DateTime.UtcNow.AddHours(23); // TVDB tokens ~24h
                _logger.LogDebug("[TvdbProvider] Authenticated successfully.");
                return _cachedToken;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[TvdbProvider] Login failed.");
        }

        return null;
    }

    // ── API helpers ──────────────────────────────────────────────────────────

    private HttpClient CreateClient()
    {
        return _httpClientFactory?.CreateClient("Tvdb")
            ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }

    private async Task<int?> SearchSeriesAsync(
        string title, string token, CancellationToken ct)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);

        var url = $"{_options.ApiBaseUrl}/search" +
                  $"?query={Uri.EscapeDataString(title)}" +
                  $"&type=series";

        TvdbSearchResponse? response;
        try
        {
            response = await client.GetFromJsonAsync<TvdbSearchResponse>(url, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[TvdbProvider] Search failed for '{Title}'.", title);
            return null;
        }

        if (response?.Data is null || response.Data.Count == 0)
            return null;

        // The search result has an `id` field (string) that's the TVDB ID.
        // Parse it as int since episode endpoints use numeric IDs.
        var first = response.Data[0];
        if (int.TryParse(first.Id, out var id))
            return id;

        _logger.LogDebug(
            "[TvdbProvider] Could not parse TVDB ID '{RawId}' for '{Title}'.",
            first.Id, title);
        return null;
    }

    private async Task<List<TvdbEpisode>?> GetDvdEpisodesAsync(
        int seriesId, int season, string token, CancellationToken ct)
    {
        return await GetEpisodesAsync(seriesId, season, "dvd", token, ct);
    }

    private async Task<List<TvdbEpisode>?> GetDefaultEpisodesAsync(
        int seriesId, int season, string token, CancellationToken ct)
    {
        return await GetEpisodesAsync(seriesId, season, "default", token, ct);
    }

    private async Task<List<TvdbEpisode>?> GetEpisodesAsync(
        int seriesId, int season, string orderType, string token, CancellationToken ct)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);

        var allEpisodes = new List<TvdbEpisode>();
        var page = 0;

        while (true)
        {
            var url = $"{_options.ApiBaseUrl}/series/{seriesId}/episodes/{orderType}" +
                      $"?season={season}&page={page}";

            TvdbEpisodesResponse? response;
            try
            {
                response = await client.GetFromJsonAsync<TvdbEpisodesResponse>(url, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "[TvdbProvider] Failed to get episodes for series {SeriesId} S{Season} ({Order}).",
                    seriesId, season, orderType);
                return allEpisodes.Count > 0 ? allEpisodes : null;
            }

            if (response?.Data?.Episodes is null || response.Data.Episodes.Count == 0)
                break;

            allEpisodes.AddRange(response.Data.Episodes);

            // Check for more pages
            if (response.Data.Episodes.Count < 500 || response.Links?.Next is null)
                break;

            page++;
        }

        // Filter to episodes with a valid number (skip extras like S00E00 placeholders)
        var valid = allEpisodes
            .Where(e => e.Number > 0)
            .OrderBy(e => e.Number)
            .ToList();

        return valid;
    }

    // ── API DTOs ─────────────────────────────────────────────────────────────

    private sealed class TvdbLoginResponse
    {
        [JsonPropertyName("data")]
        public TvdbLoginData? Data { get; set; }
    }

    private sealed class TvdbLoginData
    {
        [JsonPropertyName("token")]
        public string? Token { get; set; }
    }

    private sealed class TvdbSearchResponse
    {
        [JsonPropertyName("data")]
        public List<TvdbSearchResult>? Data { get; set; }
    }

    private sealed class TvdbSearchResult
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("first_air_time")]
        public string? FirstAirTime { get; set; }
    }

    private sealed class TvdbEpisodesResponse
    {
        [JsonPropertyName("data")]
        public TvdbEpisodesData? Data { get; set; }

        [JsonPropertyName("links")]
        public TvdbLinks? Links { get; set; }
    }

    private sealed class TvdbEpisodesData
    {
        [JsonPropertyName("episodes")]
        public List<TvdbEpisode>? Episodes { get; set; }
    }

    private sealed class TvdbEpisode
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("seasonNumber")]
        public int SeasonNumber { get; set; }

        [JsonPropertyName("number")]
        public int Number { get; set; }

        [JsonPropertyName("runtime")]
        public int? Runtime { get; set; }

        [JsonPropertyName("image")]
        public string? Image { get; set; }

        [JsonPropertyName("aired")]
        public string? Aired { get; set; }
    }

    private sealed class TvdbLinks
    {
        [JsonPropertyName("next")]
        public string? Next { get; set; }
    }
}
