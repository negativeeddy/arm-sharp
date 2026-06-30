using ArmMedia.Core.Models;
using ArmMedia.Linting.Abstractions;
using ArmMedia.Linting.Models;

namespace ArmMedia.Linting.Rules;

// ─────────────────────────────────────────────────────────────────────────────
// TV001 — Duplicate episode assignment
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// TV001: Two or more tracks are assigned to the same season/episode number.
/// This will cause one file to overwrite the other during naming.
/// </summary>
public sealed class DuplicateEpisodeLintRule : ILintRule
{
    /// <inheritdoc/>
    public string RuleId => "TV001";

    /// <inheritdoc/>
    public IEnumerable<LintIssue> Evaluate(EpisodeMap map, LintOptions options)
    {
        var seen = new Dictionary<string, int>(); // "S01E03" → trackIndex
        foreach (var track in map.Tracks)
        {
            if (track.IsExtra) continue;
            foreach (var ep in track.Episodes)
            {
                string key = $"S{track.Season:D2}E{ep:D2}";
                if (seen.TryGetValue(key, out int priorTrack))
                {
                    yield return new LintIssue
                    {
                        Severity   = LintSeverity.Error,
                        RuleId     = RuleId,
                        Message    = $"Duplicate episode {key}: tracks {priorTrack} and {track.TrackIndex} both assigned to this episode.",
                        TrackIndex = track.TrackIndex
                    };
                }
                else
                {
                    seen[key] = track.TrackIndex;
                }
            }
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// TV002 — Episode sequence gap
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// TV002: There is a gap in the episode number sequence (e.g., E01, E02, E04 with no E03).
/// </summary>
public sealed class EpisodeGapLintRule : ILintRule
{
    /// <inheritdoc/>
    public string RuleId => "TV002";

    /// <inheritdoc/>
    public IEnumerable<LintIssue> Evaluate(EpisodeMap map, LintOptions options)
    {
        var episodeNumbers = map.Tracks
            .Where(t => !t.IsExtra)
            .SelectMany(t => t.Episodes)
            .Distinct()
            .OrderBy(e => e)
            .ToList();

        for (int i = 1; i < episodeNumbers.Count; i++)
        {
            int expected = episodeNumbers[i - 1] + 1;
            if (episodeNumbers[i] != expected)
            {
                yield return new LintIssue
                {
                    Severity = LintSeverity.Warning,
                    RuleId   = RuleId,
                    Message  = $"Episode sequence gap detected: expected E{expected:D2} but found E{episodeNumbers[i]:D2}. " +
                               $"Missing episodes: {string.Join(", ", Enumerable.Range(expected, episodeNumbers[i] - expected).Select(e => $"E{e:D2}"))}"
                };
            }
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// TV004 — Positional fallback identification
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// TV004: One or more tracks were identified only by positional fallback (Low confidence).
/// Manual review is recommended.
/// </summary>
public sealed class LowConfidenceLintRule : ILintRule
{
    /// <inheritdoc/>
    public string RuleId => "TV004";

    /// <inheritdoc/>
    public IEnumerable<LintIssue> Evaluate(EpisodeMap map, LintOptions options)
    {
        foreach (var track in map.Tracks.Where(t => t.Confidence == Confidence.Low))
        {
            yield return new LintIssue
            {
                Severity   = LintSeverity.Info,
                RuleId     = RuleId,
                Message    = $"Track {track.TrackIndex} was identified by positional fallback only. Manual review recommended.",
                TrackIndex = track.TrackIndex
            };
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// TV006 — Season number mismatch
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// TV006: One or more tracks have a season number that differs from the disc-level season.
/// </summary>
public sealed class SeasonMismatchLintRule : ILintRule
{
    /// <inheritdoc/>
    public string RuleId => "TV006";

    /// <inheritdoc/>
    public IEnumerable<LintIssue> Evaluate(EpisodeMap map, LintOptions options)
    {
        foreach (var track in map.Tracks.Where(t => !t.IsExtra && t.Season != map.Season))
        {
            yield return new LintIssue
            {
                Severity   = LintSeverity.Error,
                RuleId     = RuleId,
                Message    = $"Track {track.TrackIndex} has season {track.Season} but disc-level season is {map.Season}.",
                TrackIndex = track.TrackIndex
            };
        }
    }
}
