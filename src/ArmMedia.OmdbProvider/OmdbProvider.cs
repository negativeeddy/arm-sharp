using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using ArmMedia.Core.Abstractions;
using ArmMedia.Core.Models;
using Microsoft.Extensions.Logging;

namespace ArmMedia.OmdbProvider;

/// <summary>
/// An <see cref="IEpisodeIdentificationProvider"/> that queries the OMDB API
/// to identify TV episodes by series title and season number.
/// Uses the already-configured OMDB API key from host settings.
/// Returns results with <see cref="Confidence.High"/>.
/// </summary>
[ArmMedia.Core.DiagnosticName(DiagnosticCategory)]
public sealed class OmdbProvider : IEpisodeIdentificationProvider
{
    private const string DiagnosticCategory = "OmdbProvider";
    private readonly IOmdbApiKeySource       _apiKeySource;
    private readonly ILogger                 _logger;
    private readonly IHttpClientFactory?     _httpClientFactory;

    /// <summary>Initialises the provider with an API key source and logger.</summary>
    public OmdbProvider(
        IOmdbApiKeySource          apiKeySource,
        ILoggerFactory             loggerFactory,
        IHttpClientFactory?        httpClientFactory = null)
    {
        _apiKeySource      = apiKeySource;
        _logger            = loggerFactory.CreateLogger(DiagnosticCategory);
        _httpClientFactory = httpClientFactory;
    }

    /// <inheritdoc/>
    public string ProviderName => "Omdb";

    /// <inheritdoc/>
    public async Task<ProviderResult[]> IdentifyAsync(
        DiscContext       context,
        CancellationToken cancellationToken = default)
    {
        var apiKey = _apiKeySource.GetApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogDebug("[OmdbProvider] No API key configured; skipping.");
            return [];
        }

        if (string.IsNullOrWhiteSpace(context.SeriesTitle))
        {
            _logger.LogDebug("[OmdbProvider] No series title in context; skipping.");
            return [];
        }

        // ── Step 1: Fetch the full season episode list ───────────────────────
        var episodes = await GetSeasonEpisodesAsync(
            context.SeriesTitle, context.Season, apiKey, cancellationToken);

        if (episodes is null || episodes.Count == 0)
        {
            _logger.LogInformation(
                "[OmdbProvider] No episodes found for '{Title}' S{Season}.",
                context.SeriesTitle, context.Season);
            return [];
        }

        _logger.LogInformation(
            "[OmdbProvider] Loaded {Count} episodes for '{Title}' S{Season}.",
            episodes.Count, context.SeriesTitle, context.Season);

        // ── Step 2: Map tracks to episodes sequentially ──────────────────────
        // DvdCompare (which runs last in the ProviderOrder) handles per-disc
        // episode numbering. Sequential assignment from episode 1 is safer than
        // the old (discNumber - 1) * episodesPerDisc math which produces wrong
        // offsets when discs have varying episode counts.
        var episodeTracks = context.Tracks
            .Where(t => t.Duration.TotalSeconds >= 120)
            .OrderBy(t => t.TrackIndex)
            .ToList();

        var offset = (context.StartingEpisodeNumber ?? 1) - 1; // 0-based index into episodes[]
        if (context.StartingEpisodeNumber is not null)
        {
            _logger.LogInformation(
                "[OmdbProvider] Using manual starting episode offset {Offset}.",
                context.StartingEpisodeNumber);
        }

        _logger.LogInformation(
            "[OmdbProvider] Disc {Disc}: {EpsPerDisc} episode tracks, mapping sequentially.",
            context.DiscNumber, episodeTracks.Count);

        // ── Step 3: Map tracks to episodes ───────────────────────────────────
        var results = new List<ProviderResult>();

        for (int i = 0; i < episodeTracks.Count; i++)
        {
            var track = episodeTracks[i];

            var episodeIndex = offset + i;
            if (episodeIndex < episodes.Count)
            {
                var ep = episodes[episodeIndex];
                results.Add(new ProviderResult
                {
                    TrackIndex   = track.TrackIndex,
                    Season       = context.Season,
                    Episodes     = [ep.EpisodeNumber],
                    Title        = ep.Title,
                    IsExtra      = false,
                    Confidence   = Confidence.High,
                    ProviderName = ProviderName
                });

                _logger.LogDebug(
                    "[OmdbProvider] Track {TrackIdx} → S{Season}E{Ep} '{Title}'",
                    track.TrackIndex, context.Season,
                    ep.EpisodeNumber, ep.Title);
            }
            else
            {
                _logger.LogDebug(
                    "[OmdbProvider] No episode match for track {TrackIdx} (index {Idx} beyond {Max}).",
                    track.TrackIndex, i, episodes.Count);
            }
        }

        _logger.LogInformation(
            "[OmdbProvider] Mapped {Count}/{Total} tracks from OMDB.",
            results.Count, context.Tracks.Count);

        return results.ToArray();
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private HttpClient CreateClient()
    {
        return _httpClientFactory?.CreateClient("Omdb")
            ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }

    private async Task<List<OmdbEpisode>?> GetSeasonEpisodesAsync(
        string title, int season, string apiKey, CancellationToken ct)
    {
        var client = CreateClient();
        var url = $"http://www.omdbapi.com/" +
                  $"?apikey={apiKey}" +
                  $"&t={Uri.EscapeDataString(title)}" +
                  $"&Season={season}";

        OmdbSeasonResponse? response;
        try
        {
            response = await client.GetFromJsonAsync<OmdbSeasonResponse>(url, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[OmdbProvider] Season lookup failed for '{Title}' S{Season}.",
                title, season);
            return null;
        }

        if (response?.Episodes is null || response.Episodes.Count == 0)
            return null;

        if (response.Response != "True")
            return null;

        return response.Episodes
            .Where(e => int.TryParse(e.Episode, out _))
            .Select(e => new OmdbEpisode
            {
                Title         = e.Title ?? "Untitled",
                EpisodeNumber = int.Parse(e.Episode!),
                ImdbId        = e.ImdbId
            })
            .OrderBy(e => e.EpisodeNumber)
            .ToList();
    }

    // ── API DTOs ─────────────────────────────────────────────────────────────

    private sealed class OmdbSeasonResponse
    {
        [JsonPropertyName("Response")]
        public string? Response { get; set; }

        [JsonPropertyName("Title")]
        public string? Title { get; set; }

        [JsonPropertyName("Season")]
        public string? Season { get; set; }

        [JsonPropertyName("totalSeasons")]
        public string? TotalSeasons { get; set; }

        [JsonPropertyName("Episodes")]
        public List<OmdbEpisodeRaw>? Episodes { get; set; }
    }

    private sealed class OmdbEpisodeRaw
    {
        [JsonPropertyName("Title")]
        public string? Title { get; set; }

        [JsonPropertyName("Episode")]
        public string? Episode { get; set; }

        [JsonPropertyName("Released")]
        public string? Released { get; set; }

        [JsonPropertyName("imdbRating")]
        public string? ImdbRating { get; set; }

        [JsonPropertyName("imdbID")]
        public string? ImdbId { get; set; }
    }

    private sealed class OmdbEpisode
    {
        public string? Title { get; init; }
        public required int EpisodeNumber { get; init; }
        public string? ImdbId { get; init; }
    }
}
