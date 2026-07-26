using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using ArmMedia.Core.Abstractions;
using ArmMedia.Core.Models;
using ArmMedia.TmdbProvider.Models;
using Microsoft.Extensions.Logging;

namespace ArmMedia.TmdbProvider;

/// <summary>
/// An <see cref="IEpisodeIdentificationProvider"/> that queries TheMovieDB (TMDB)
/// to identify TV episodes by series title and season number.
/// Uses series details for season validation, name-similarity scoring for
/// search result ranking, and duration filtering to skip extras.
/// </summary>
public sealed class TmdbProvider : IEpisodeIdentificationProvider
{
    private readonly ITmdbApiKeySource          _apiKeySource;
    private readonly ITitleNormalizer?          _titleNormalizer;
    private readonly ILogger<TmdbProvider>      _logger;
    private readonly IHttpClientFactory?        _httpClientFactory;

    private const string BaseUrl = "https://api.themoviedb.org/3";

    /// <summary>
    /// Minimum track duration in seconds to be considered an episode track.
    /// Tracks shorter than this are likely extras, trailers, or menu items.
    /// </summary>
    private const double MinEpisodeDurationSeconds = 120;

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
                "[TmdbProvider] Title normalized: '{Raw}' → query='{Query}', season={Season}, disc={Disc}, edition='{Edition}'.",
                context.SeriesTitle, norm.Query, norm.Season, norm.Disc, norm.Edition);

            if (!string.IsNullOrWhiteSpace(norm.Query))
                normalizedTitle = norm.Query;

            if (norm.Season is int s && context.Season <= 1)
                seasonOverride = s;
        }

        // ── Step 1: Search for the TV series (with scoring) ───────────────────
        var (seriesId, seriesName) = await SearchSeriesAsync(normalizedTitle, apiKey, cancellationToken);
        if (seriesId is null)
        {
            if (normalizedTitle != context.SeriesTitle)
            {
                _logger.LogDebug(
                    "[TmdbProvider] Normalized search returned no results; retrying with original title '{Title}'.",
                    context.SeriesTitle);
                (seriesId, seriesName) = await SearchSeriesAsync(context.SeriesTitle, apiKey, cancellationToken);
            }

            if (seriesId is null)
            {
                _logger.LogInformation(
                    "[TmdbProvider] No TMDB series found for '{Title}'.",
                    context.SeriesTitle);
                return [];
            }
        }

        var effectiveSeason = seasonOverride ?? context.Season;

        _logger.LogInformation(
            "[TmdbProvider] Found TMDB series '{Name}' (ID {SeriesId}).",
            seriesName, seriesId);

        // ── Step 2: Fetch series details and validate season ──────────────────
        var tvDetails = await GetTvDetailsAsync(seriesId.Value, apiKey, cancellationToken);
        if (tvDetails is not null)
        {
            _logger.LogInformation(
                "[TmdbProvider] Series has {Seasons} seasons, {Episodes} total episodes.",
                tvDetails.NumberOfSeasons, tvDetails.NumberOfEpisodes);

            // Validate that the requested season exists and has episodes
            var seasonSummary = tvDetails.Seasons?.FirstOrDefault(s => s.SeasonNumber == effectiveSeason);
            if (seasonSummary is null)
            {
                _logger.LogWarning(
                    "[TmdbProvider] Season {Season} does not exist for series '{Name}' (has {Count} seasons).",
                    effectiveSeason, seriesName, tvDetails.NumberOfSeasons);
                return [];
            }

            if (seasonSummary.EpisodeCount == 0)
            {
                _logger.LogWarning(
                    "[TmdbProvider] Season {Season} exists for '{Name}' but has 0 episodes.",
                    effectiveSeason, seriesName);
                return [];
            }

            _logger.LogInformation(
                "[TmdbProvider] Season {Season} '{SeasonName}' has {EpisodeCount} episodes.",
                effectiveSeason, seasonSummary.Name, seasonSummary.EpisodeCount);
        }

        // ── Step 3: Get season episodes ───────────────────────────────────────
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

        // ── Step 4: Map episode tracks to episodes by position ────────────────
        // Only map tracks long enough to be episodes (>=120s),
        // skipping extras, trailers, and menu items.
        var episodeTracks = context.Tracks
            .Where(t => t.Duration.TotalSeconds >= MinEpisodeDurationSeconds)
            .OrderBy(t => t.TrackIndex)
            .ToList();

        var results = new List<ProviderResult>();
        var offset = (context.StartingEpisodeNumber ?? 1) - 1;

        for (int i = 0; i < episodeTracks.Count; i++)
        {
            var track = episodeTracks[i];

            var episodeIndex = offset + i;
            TmdbEpisode? matchingEpisode = null;
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
                _logger.LogDebug(
                    "[TmdbProvider] No episode mapping for track {TrackIdx} (beyond season episode count).",
                    track.TrackIndex);
            }
        }

        _logger.LogInformation(
            "[TmdbProvider] Mapped {Count}/{Total} episode tracks from TMDB.",
            results.Count, episodeTracks.Count);

        return results.ToArray();
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private HttpClient CreateClient()
    {
        return _httpClientFactory?.CreateClient("Tmdb")
            ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }

    /// <summary>
    /// Searches TMDB for a TV series and returns the best match.
    /// Uses name-similarity scoring to avoid picking unrelated series
    /// that happen to be first in the results.
    /// </summary>
    private async Task<(int? Id, string? Name)> SearchSeriesAsync(
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
            return (null, null);
        }

        if (response?.Results is null || response.Results.Count == 0)
            return (null, null);

        // Score each result by name similarity and pick the best match
        var best = response.Results
            .Select(r => new { r.Id, r.Name, Score = ScoreSeriesMatch(title, r.Name) })
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Id)
            .First();

        _logger.LogDebug(
            "[TmdbProvider] Best match for '{Title}': '{Name}' (ID {Id}, score {Score:F2}).",
            title, best.Name, best.Id, best.Score);

        return (best.Id, best.Name);
    }

    /// <summary>
    /// Fetches full series details from TMDB, including the season list.
    /// </summary>
    private async Task<TmdbTvDetails?> GetTvDetailsAsync(
        int seriesId, string apiKey, CancellationToken ct)
    {
        var client = CreateClient();
        var url = $"{BaseUrl}/tv/{seriesId}?api_key={apiKey}";

        try
        {
            return await client.GetFromJsonAsync<TmdbTvDetails>(url, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[TmdbProvider] Failed to get TV details for series {SeriesId}.", seriesId);
            return null;
        }
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

    // ── Series matching ───────────────────────────────────────────────────────

    /// <summary>
    /// Scores how well a TMDB search result matches the expected title.
    /// Returns a value between 0 and 1 where 1 is a perfect match.
    /// </summary>
    internal static double ScoreSeriesMatch(string expected, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return 0;

        var a = NormalizeForComparison(expected);
        var b = NormalizeForComparison(candidate);

        if (a == b)
            return 1.0;

        // One contains the other
        if (a.Contains(b, StringComparison.Ordinal))
            return 0.85;
        if (b.Contains(a, StringComparison.Ordinal))
            return 0.8;

        // Token overlap
        var tokensA = a.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var tokensB = b.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (tokensA.Length == 0 || tokensB.Length == 0)
            return 0;

        int matched = tokensA.Count(t => tokensB.Contains(t));
        double jaccard = (double)matched / (tokensA.Length + tokensB.Length - matched);

        // Boost if all query tokens are found in the candidate
        bool allQueryTokensPresent = tokensA.All(t => tokensB.Contains(t));
        if (allQueryTokensPresent && jaccard > 0)
            jaccard = Math.Max(jaccard, 0.7);

        return jaccard;
    }

    private static string NormalizeForComparison(string title)
    {
        var normalized = title.ToLowerInvariant().Trim();
        return System.Text.RegularExpressions.Regex.Replace(normalized, @"[^a-z0-9\s]", " ")
            .Replace("  ", " ", StringComparison.Ordinal)
            .Trim();
    }

    // ── API DTOs (private — public models in Models/ folder) ──────────────────

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
}
