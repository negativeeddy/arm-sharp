using System.Net.Http;
using System.Text.RegularExpressions;
using ArmMedia.Core.Abstractions;
using ArmMedia.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArmMedia.DvdCompareProvider;

/// <summary>
/// An <see cref="IEpisodeIdentificationProvider"/> that fetches a TV series
/// season comparison page from dvdcompare.net and matches tracks to episodes
/// by runtime (duration). This is particularly useful for DVD releases where
/// MakeMKV produces generic track filenames but the episode structure and
/// runtimes are well-documented on dvdcompare.net.
/// </summary>
public sealed partial class DvdCompareProvider : IEpisodeIdentificationProvider
{
    private readonly DvdCompareProviderOptions   _options;
    private readonly ILogger<DvdCompareProvider>  _logger;
    private readonly IHttpClientFactory?          _httpClientFactory;

    /// <summary>Initialises the provider with options, logger, and optional HTTP client factory.</summary>
    public DvdCompareProvider(
        IOptions<DvdCompareProviderOptions>   options,
        ILogger<DvdCompareProvider>           logger,
        IHttpClientFactory?                   httpClientFactory = null)
    {
        _options            = options.Value;
        _logger             = logger;
        _httpClientFactory  = httpClientFactory;
    }

    /// <inheritdoc/>
    public string ProviderName => "DvdCompare";

    /// <inheritdoc/>
    public async Task<ProviderResult[]> IdentifyAsync(
        DiscContext       context,
        CancellationToken cancellationToken = default)
    {
        // ── Step 0: Search dvdcompare.net for the series ───────────────────
        if (string.IsNullOrWhiteSpace(context.SeriesTitle))
        {
            _logger.LogDebug("[DvdCompareProvider] No SeriesTitle; skipping.");
            return [];
        }

        _logger.LogInformation(
            "[DvdCompareProvider] Searching dvdcompare.net for '{Series}' season {Season}.",
            context.SeriesTitle, context.Season);

        string? comparisonUrl = await SearchComparisonUrlAsync(context.SeriesTitle, context.Season, cancellationToken);

        if (string.IsNullOrWhiteSpace(comparisonUrl))
        {
            _logger.LogWarning(
                "[DvdCompareProvider] No dvdcompare.net page found for '{Series}' season {Season}.",
                context.SeriesTitle, context.Season);
            return [];
        }

        _logger.LogInformation(
            "[DvdCompareProvider] Found comparison page: {Url}", comparisonUrl);

        // ── Step 1: Fetch and parse the comparison page ───────────────────────
        List<DiscEpisodeGroup>? discGroups;
        try
        {
            var html = await FetchPageAsync(comparisonUrl, cancellationToken);
            if (string.IsNullOrWhiteSpace(html))
            {
                _logger.LogWarning("[DvdCompareProvider] Empty response from {Url}.", comparisonUrl);
                return [];
            }

            discGroups = ParseDiscEpisodeGroups(html, _options.ReleaseIndex);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[DvdCompareProvider] Failed to fetch or parse {Url}.", comparisonUrl);
            return [];
        }

        if (discGroups is null || discGroups.Count == 0)
        {
            _logger.LogWarning("[DvdCompareProvider] No disc/episode data found at {Url}.", comparisonUrl);
            return [];
        }

        _logger.LogInformation(
            "[DvdCompareProvider] Found {DiscCount} disc(s) with {TotalEpisodes} total episode(s).",
            discGroups.Count,
            discGroups.Sum(d => d.Episodes.Count));

        // ── Step 2: Compute episode numbers across all discs ──────────────────
        // Episode numbers are sequential: Disc 1 → episodes 1..N, Disc 2 → N+1..M, etc.
        int episodeCounter = 0;
        foreach (var disc in discGroups)
        {
            for (int i = 0; i < disc.Episodes.Count; i++)
            {
                episodeCounter++;
                var ep = disc.Episodes[i];
                disc.Episodes[i] = ep with { EpisodeNumber = episodeCounter };
            }
        }

        // ── Step 3: Find the disc group for the current disc ──────────────────
        // DiscContext.DiscNumber is 1-based.
        var targetDisc = discGroups.ElementAtOrDefault(context.DiscNumber - 1);

        if (targetDisc is null)
        {
            _logger.LogDebug(
                "[DvdCompareProvider] Disc number {DiscNumber} not found (page has {DiscCount} disc(s)).",
                context.DiscNumber, discGroups.Count);
            return [];
        }

        _logger.LogInformation(
            "[DvdCompareProvider] Matching {TrackCount} track(s) against disc {DiscNumber} ({EpCount} episode(s)).",
            context.Tracks.Count, context.DiscNumber, targetDisc.Episodes.Count);

        // ── Step 4: Match tracks to episodes by runtime ───────────────────────
        int toleranceSeconds = Math.Max(_options.RuntimeToleranceSeconds, 10);
        var results = new List<ProviderResult>();
        var usedEpisodes = new HashSet<int>(); // track used episode indices

        // Sort tracks by index to match in order
        var orderedTracks = context.Tracks.OrderBy(t => t.TrackIndex).ToList();

        foreach (var track in orderedTracks)
        {
            var trackDurationSec = track.Duration.TotalSeconds;

            // Find the best-matching episode by runtime
            DvdEpisode? bestMatch = null;
            double bestDiff = double.MaxValue;

            for (int i = 0; i < targetDisc.Episodes.Count; i++)
            {
                if (usedEpisodes.Contains(i))
                    continue; // already assigned

                var ep = targetDisc.Episodes[i];
                double diff = Math.Abs(trackDurationSec - ep.DurationSeconds);

                if (diff <= toleranceSeconds && diff < bestDiff)
                {
                    bestDiff = diff;
                    bestMatch = ep;
                }
            }

            if (bestMatch is not null)
            {
                int matchIndex = targetDisc.Episodes.IndexOf(bestMatch);
                usedEpisodes.Add(matchIndex);

                _logger.LogDebug(
                    "[DvdCompareProvider] Track {TrackIdx} ({Duration:mm\\:ss}) → S{Season}E{Ep} '{Title}' (diff {Diff:F0}s)",
                    track.TrackIndex,
                    track.Duration,
                    context.Season,
                    bestMatch.EpisodeNumber,
                    bestMatch.Title,
                    bestDiff);

                results.Add(new ProviderResult
                {
                    TrackIndex   = track.TrackIndex,
                    Season       = context.Season,
                    Episodes     = [bestMatch.EpisodeNumber],
                    Title        = bestMatch.Title,
                    IsExtra      = false,
                    Confidence   = Confidence.High,
                    ProviderName = ProviderName
                });
            }
            else
            {
                _logger.LogDebug(
                    "[DvdCompareProvider] Track {TrackIdx} ({Duration:mm\\:ss}) — no matching episode within tolerance.",
                    track.TrackIndex,
                    track.Duration);
            }
        }

        _logger.LogInformation(
            "[DvdCompareProvider] Matched {Matched}/{Total} tracks from dvdcompare.net.",
            results.Count, context.Tracks.Count);

        return results.ToArray();
    }

    // ── HTTP ──────────────────────────────────────────────────────────────────

    private HttpClient CreateClient()
    {
        var client = _httpClientFactory?.CreateClient("DvdCompare")
            ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        return client;
    }

    private async Task<string?> FetchPageAsync(string url, CancellationToken ct)
    {
        var client = CreateClient();
        return await client.GetStringAsync(url, ct);
    }

    // ── Search ────────────────────────────────────────────────────────────────

    private const string SearchBaseUrl = "https://dvdcompare.net/comparisons/search.php";

    /// <summary>
    /// Searches dvdcompare.net for the given series title and season, returning
    /// the URL of the best-matching comparison page, or <c>null</c> if no match
    /// is found.
    /// </summary>
    private async Task<string?> SearchComparisonUrlAsync(
        string seriesTitle, int season, CancellationToken ct)
    {
        string searchUrl = $"{SearchBaseUrl}?param={Uri.EscapeDataString(seriesTitle)}&searchtype=text";
        string? html;

        try
        {
            html = await FetchPageAsync(searchUrl, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[DvdCompareProvider] Search failed for '{Title}'.", seriesTitle);
            return null;
        }

        if (string.IsNullOrWhiteSpace(html))
            return null;

        return ParseSearchResults(html, seriesTitle, season);
    }

    /// <summary>
    /// Parses dvdcompare.net search results to find the page matching the given
    /// series title and season number. Returns the absolute URL, or <c>null</c>.
    /// </summary>
    public static string? ParseSearchResults(string html, string seriesTitle, int season)
    {
        var matches = SearchResultPattern().Matches(html);
        if (matches.Count == 0)
            return null;

        // Build a lower-case title for fuzzy matching
        string searchTitle = seriesTitle.Trim().ToLowerInvariant();

        // Track whether any result has season info — if none do, fall back to
        // title-only matching (for pages where season isn't in the link text).
        bool anyResultHasSeasonInfo = false;
        string? titleOnlyFallback = null;

        foreach (Match match in matches)
        {
            string fid = match.Groups[1].Value;
            string linkText = match.Groups[2].Value.Trim();
            string lowerText = linkText.ToLowerInvariant();

            if (!lowerText.Contains(searchTitle))
                continue;

            // The link text is typically "Series Name: Season N (TV) (year-year)"
            // Match "Season N" to find the correct season.
            var seasonMatch = System.Text.RegularExpressions.Regex.Match(
                linkText, @"Season\s+(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (seasonMatch.Success)
            {
                anyResultHasSeasonInfo = true;

                if (int.TryParse(seasonMatch.Groups[1].Value, out int foundSeason) &&
                    foundSeason == season)
                {
                    return $"https://dvdcompare.net/comparisons/film.php?fid={fid}";
                }
            }
            else
            {
                // Title match without season info — save as potential fallback
                titleOnlyFallback ??= fid;
            }
        }

        // If at least one result had season info but none matched, don't guess.
        if (anyResultHasSeasonInfo)
            return null;

        // No results had season info; return the first title-only match as a best guess.
        if (titleOnlyFallback is not null)
            return $"https://dvdcompare.net/comparisons/film.php?fid={titleOnlyFallback}";

        return null;
    }

    /// <summary>
    /// Regex that matches a dvdcompare.net search result link.
    /// Group 1: the numeric FID; Group 2: the link text (title).
    /// </summary>
    [GeneratedRegex(
        @"<a\s+href=""film\.php\?fid=(\d+)""[^>]*>([^<]+)</a>",
        RegexOptions.IgnoreCase)]
    private static partial Regex SearchResultPattern();

    // ── HTML Parsing ──────────────────────────────────────────────────────────

    /// <summary>
    /// Regex that captures the entire Extras description block.
    /// Group 1: the content between <c>&lt;div class="description"&gt;</c> and <c>&lt;/div&gt;</c>
    /// following a <c>&lt;div class="label"&gt;Extras:&lt;/div&gt;</c>.
    /// </summary>
    [GeneratedRegex(
        @"<div\s+class=""label"">Extras:</div>\s*<div\s+class=""description"">(.*?)</div>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ExtrasSectionPattern();

    /// <summary>
    /// Regex that matches a disc header like <b>DISC ONE</b> or <b>DISC 1</b>
    /// and captures the disc number word/phrase.
    /// </summary>
    [GeneratedRegex(
        @"<b>\s*DISC\s+(ONE|TWO|THREE|FOUR|FIVE|SIX|SEVEN|EIGHT|NINE|TEN|\d+)\s*</b>",
        RegexOptions.IgnoreCase)]
    private static partial Regex DiscHeaderPattern();

    /// <summary>
    /// Regex that matches an episode line: <c>- "Episode Title" (MM:SS)</c>
    /// Group 1: episode title; Group 2: minutes; Group 3: seconds.
    /// </summary>
    [GeneratedRegex(
        @"-\s*""(.*?)""\s*\((\d+):(\d+)\)",
        RegexOptions.IgnoreCase)]
    private static partial Regex EpisodeLinePattern();
    /// <summary>
    /// Regex that captures the explicit episode count from a disc header line
    /// like <c>7 Episodes (with Play All)</c> or <c>3 Episodes (with Play All) (67:04)</c>.
    /// Group 1: the episode count number.
    /// </summary>
    [GeneratedRegex(
        @"(\\d+)\\s+Episodes\\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex EpisodeCountPattern();
    /// <summary>
    /// Parses disc/episode groups from the HTML content of a dvdcompare.net comparison page.
    /// Uses the release at <paramref name="releaseIndex"/> (0-based).
    /// </summary>
    public static List<DiscEpisodeGroup>? ParseDiscEpisodeGroups(string html, int releaseIndex = 0)
    {
        // Find all Extras sections
        var extrasMatches = ExtrasSectionPattern().Matches(html);
        if (extrasMatches.Count == 0)
            return null;

        // Use the requested release index (clamp to valid range)
        int idx = Math.Clamp(releaseIndex, 0, extrasMatches.Count - 1);
        string content = extrasMatches[idx].Groups[1].Value;

        return ParseDiscsFromExtrasContent(content);
    }

    /// <summary>
    /// Parses disc groups from the inner content of one Extras description block.
    /// Uses the explicit "X Episodes" count from each disc header to exclude
    /// non-episode lines (e.g. deleted scenes in commentary sections) that also
    /// match the episode title/runtime pattern.
    /// </summary>
    public static List<DiscEpisodeGroup> ParseDiscsFromExtrasContent(string content)
    {
        var discs = new List<DiscEpisodeGroup>();

        // Split on disc headers. Each disc section starts with <b>DISC ...</b>
        var discMatches = DiscHeaderPattern().Matches(content);

        for (int d = 0; d < discMatches.Count; d++)
        {
            int sectionStart = discMatches[d].Index;
            int sectionEnd = (d + 1 < discMatches.Count)
                ? discMatches[d + 1].Index
                : content.Length;

            string section = content[sectionStart..sectionEnd];

            // Determine episode count from the "X Episodes" line
            var countMatch = EpisodeCountPattern().Match(section);
            int? episodeCount = countMatch.Success
                ? int.Parse(countMatch.Groups[1].Value)
                : null;

            // Parse episode lines from this section
            var epMatches = EpisodeLinePattern().Matches(section);
            var episodes = new List<DvdEpisode>();

            for (int i = 0; i < epMatches.Count; i++)
            {
                // Stop if we've reached the explicit episode count
                if (episodeCount.HasValue && episodes.Count >= episodeCount.Value)
                    break;

                string title = epMatches[i].Groups[1].Value.Trim();
                int minutes = int.Parse(epMatches[i].Groups[2].Value);
                int seconds = int.Parse(epMatches[i].Groups[3].Value);

                episodes.Add(new DvdEpisode
                {
                    Title = title,
                    DurationSeconds = minutes * 60 + seconds
                });
            }

            if (episodes.Count > 0)
            {
                discs.Add(new DiscEpisodeGroup
                {
                    DiscNumber = d + 1, // 1-based
                    Episodes = episodes
                });
            }
        }

        return discs;
    }
}

// ── Internal DTOs ────────────────────────────────────────────────────────────

/// <summary>
/// Represents a single disc within a DVD release, containing a list of episodes.
/// </summary>
public sealed class DiscEpisodeGroup
{
    /// <summary>1-based disc number within the release.</summary>
    public int DiscNumber { get; set; }

    /// <summary>Episodes on this disc, in order.</summary>
    public List<DvdEpisode> Episodes { get; set; } = [];
}

/// <summary>
/// Represents a single episode parsed from a dvdcompare.net listing.
/// EpisodeNumber is populated after computing sequential numbering across all discs.
/// </summary>
public sealed record DvdEpisode
{
    /// <summary>Episode title as listed on dvdcompare.net.</summary>
    public required string Title { get; init; }

    /// <summary>Duration in seconds as listed on dvdcompare.net.</summary>
    public required double DurationSeconds { get; init; }

    /// <summary>
    /// Computed episode number (1-based, sequential across all discs in the release).
    /// Set after parsing all discs.
    /// </summary>
    public int EpisodeNumber { get; set; }
}
