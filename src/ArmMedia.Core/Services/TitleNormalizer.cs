using System.Text.RegularExpressions;
using ArmMedia.Core.Abstractions;

namespace ArmMedia.Core.Services;

/// <summary>
/// Normalizes raw disc info titles into clean TMDB search queries.
/// Extracts season, disc, edition, and episode hints from noisy title strings.
/// <para>
/// Examples:
/// <list type="bullet">
///   <item><c>"Season 3 Disc 2"</c> → Query=<c>""</c>, Season=3, Disc=2</item>
///   <item><c>"S3E05 Pilot"</c> → Query=<c>"pilot"</c>, Season=3, EpisodeHint="5"</item>
///   <item><c>"Blu-Ray Edition"</c> → Query=<c>""</c>, Edition="bluray"</item>
/// </list>
/// </para>
/// </summary>
public sealed partial class TitleNormalizer : ITitleNormalizer
{
    // ── Season patterns ────────────────────────────────────────────────────────
    // Matches: "Season 3", "saison 3", "temporada 3", and underscore-separated
    // variants like "SEASON_1". [\s_]* treats underscores like whitespace.
    [GeneratedRegex(@"(?:season|saison|temporada)[\s_]*(\d{1,2})", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex SeasonWordPattern();

    // Matches compact: "S03", "S3" — negative lookahead prevents matching "S5" inside "S5E08"
    // when the combined pattern already handles that. Negative lookbehind for alphanumerics
    // (instead of \b) allows a preceding underscore, e.g. "Weeds_S2".
    [GeneratedRegex(@"(?<![A-Za-z0-9])S(\d{1,2})(?!\d)", RegexOptions.Compiled)]
    private static partial Regex SeasonLetterPattern();

    // ── Disc patterns ──────────────────────────────────────────────────────────
    // Matches: "Disc 2", "Disc2", "Disc_1". [\s_]* treats underscores like whitespace;
    // the alphanumeric lookbehind allows a preceding underscore ("_Disc_1").
    [GeneratedRegex(@"(?<![A-Za-z0-9])disc[\s_]*(\d{1,2})", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex DiscWordPattern();

    // Matches compact: "D2", "D02" — negative lookahead prevents "D2" matching in "D2X"
    // Negative lookbehind for letters allows matching inside "S2D1" compact notation
    // where there's no word boundary between the preceding digit and D.
    [GeneratedRegex(@"(?<![A-Za-z])D(\d{1,2})(?!\d)", RegexOptions.Compiled)]
    private static partial Regex DiscLetterPattern();

    // ── Combined Season+Episode (S01E05 style) ────────────────────────────────
    // Matches: "S01E05", "S3E08-E10", "S01E05E06"
    [GeneratedRegex(@"\bS(\d{1,2})E(\d{1,3})(?:\s*[-–]\s*(?:E)?(\d{1,3}))?", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex SeasonEpisodePattern();

    // ── Standalone Episode patterns ────────────────────────────────────────────
    // Matches: "Episode 5", "Ep 5-8", "Episodes 5,6,7"
    [GeneratedRegex(@"(?:ep(?:isode)?s?)\s*(\d{1,3}(?:\s*[-–,]\s*\d{1,3})*)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex EpisodeWordPattern();

    // Matches standalone "E05" (not preceded by a digit — avoids matching inside S01E05)
    [GeneratedRegex(@"(?<!\d)E(\d{1,3})(?!\d)", RegexOptions.Compiled)]
    private static partial Regex EpisodeLetterPattern();

    // ── Punctuation normalization ──────────────────────────────────────────────
    // Replace punctuation with whitespace, preserving apostrophes and ampersands.
    [GeneratedRegex(@"[^\w\s'&]", RegexOptions.Compiled)]
    private static partial Regex PunctuationPattern();

    [GeneratedRegex(@"\s+", RegexOptions.Compiled)]
    private static partial Regex WhitespacePattern();

    // ── Edition tokens ─────────────────────────────────────────────────────────
    // Each entry is matched case-insensitively. Hyphens in the source text are
    // treated as optional separators during matching.
    private static readonly string[] EditionTokens =
    [
        "bluray", "blu-ray", "blu ray",
        "4k", "uhd", "ultra hd",
        "dvd", "hddvd",
        "remastered", "extended", "unrated",
        "directors cut", "director's cut",
        "theatrical", "criterion", "special edition", "deluxe",
        "imax", "dbox", "3d"
    ];

    /// <inheritdoc/>
    public TitleNormalizationResult Normalize(string rawTitle)
    {
        if (string.IsNullOrWhiteSpace(rawTitle))
        {
            return new TitleNormalizationResult { Query = "" };
        }

        var text = rawTitle.Trim();

        // ── Extract combined season+episode (S01E05 style) first ────────────
        int? season = null;
        int? disc = null;
        string? episodeHint = null;
        string? edition = null;

        var seMatch = SeasonEpisodePattern().Match(text);
        if (seMatch.Success)
        {
            season = int.Parse(seMatch.Groups[1].Value);
            var epNum = seMatch.Groups[2].Value;
            if (seMatch.Groups[3].Success)
                episodeHint = $"{epNum}-{seMatch.Groups[3].Value}";
            else
                episodeHint = epNum;
        }

        // ── Extract standalone season ────────────────────────────────────────
        if (season is null)
        {
            var seasonMatch = SeasonWordPattern().Match(text);
            if (seasonMatch.Success)
            {
                season = int.Parse(seasonMatch.Groups[1].Value);
            }
            else
            {
                seasonMatch = SeasonLetterPattern().Match(text);
                if (seasonMatch.Success)
                    season = int.Parse(seasonMatch.Groups[1].Value);
            }
        }

        // ── Extract disc ──────────────────────────────────────────────────────
        var discMatch = DiscWordPattern().Match(text);
        if (discMatch.Success)
        {
            disc = int.Parse(discMatch.Groups[1].Value);
        }
        else
        {
            discMatch = DiscLetterPattern().Match(text);
            if (discMatch.Success)
                disc = int.Parse(discMatch.Groups[1].Value);
        }

        // ── Extract standalone episode (if not already found via S01E05) ─────
        if (episodeHint is null)
        {
            var episodeMatch = EpisodeWordPattern().Match(text);
            if (episodeMatch.Success)
            {
                episodeHint = episodeMatch.Groups[1].Value;
            }
            else
            {
                episodeMatch = EpisodeLetterPattern().Match(text);
                if (episodeMatch.Success)
                    episodeHint = episodeMatch.Groups[1].Value;
            }
        }

        // ── Extract edition ───────────────────────────────────────────────────
        foreach (var token in EditionTokens)
        {
            // Build flexible regex: hyphens/spaces are optional separators
            var escaped = Regex.Escape(token);
            var flexible = escaped.Replace(@"\-", @"[\s\-]?").Replace(@"\ ", @"[\s\-]?");
            var pattern = $@"(?i)\b{flexible}\b";
            if (Regex.IsMatch(text, pattern))
            {
                edition = token.Replace(" ", "").Replace("-", "");
                break;
            }
        }

        // ── Build query ───────────────────────────────────────────────────────
        var query = text;

        // Remove combined season+episode pattern (S01E05)
        query = SeasonEpisodePattern().Replace(query, " ");

        // Remove standalone season hints
        query = SeasonWordPattern().Replace(query, " ");
        query = SeasonLetterPattern().Replace(query, " ");

        // Remove disc hints
        query = DiscWordPattern().Replace(query, " ");
        query = DiscLetterPattern().Replace(query, " ");

        // Remove episode hints
        query = EpisodeWordPattern().Replace(query, " ");
        query = EpisodeLetterPattern().Replace(query, " ");

        // Remove edition tokens (flexible matching)
        foreach (var token in EditionTokens)
        {
            var escaped = Regex.Escape(token);
            var flexible = escaped.Replace(@"\-", @"[\s\-]?").Replace(@"\ ", @"[\s\-]?");
            var pattern = $@"(?i)\b{flexible}\b";
            query = Regex.Replace(query, pattern, " ");
        }

        // Normalize punctuation, whitespace, and lowercase. Underscores are filename
        // separators (e.g. "Weeds_S2_Disc_1"), so collapse them to spaces like any
        // other separator before trimming and lowercasing.
        query = PunctuationPattern().Replace(query, " ");
        query = query.Replace('_', ' ');
        query = WhitespacePattern().Replace(query, " ").Trim();
        query = query.ToLowerInvariant();

        return new TitleNormalizationResult
        {
            Query = query,
            Season = season,
            Disc = disc,
            Edition = edition,
            EpisodeHint = episodeHint
        };
    }
}
