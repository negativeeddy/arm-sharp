using ArmMedia.Core.Models;
using ArmMedia.Linting.Abstractions;
using ArmMedia.Linting.Models;

namespace ArmMedia.Linting.Rules;

/// <summary>
/// TV003 — Track duration vs expected runtime mismatch.
/// Raises a warning when a track's duration deviates significantly from the
/// configured expected episode runtime (by more than 25%).
/// </summary>
/// <remarks>
/// Requires <see cref="LintOptions.ExpectedEpisodeDurationSeconds"/> to be set to
/// a value greater than zero. When unconfigured, the rule returns no issues.
/// The expected runtime is typically obtained from TMDB or another metadata source
/// and configured per series.
/// </remarks>
public sealed class RuntimeMismatchLintRule : ILintRule
{
    /// <inheritdoc/>
    public string RuleId => "TV003";

    /// <inheritdoc/>
    public IEnumerable<LintIssue> Evaluate(EpisodeMap map, LintOptions options)
    {
        if (options.ExpectedEpisodeDurationSeconds <= 0)
            yield break; // no expected runtime configured; cannot check

        var expected = TimeSpan.FromSeconds(options.ExpectedEpisodeDurationSeconds);

        foreach (var track in map.Tracks)
        {
            if (track.IsExtra) continue;
            if (track.Duration is null) continue;

            double actualSec = track.Duration.Value.TotalSeconds;
            double diffRatio = Math.Abs(actualSec - expected.TotalSeconds) / expected.TotalSeconds;

            if (diffRatio > 0.25)
            {
                yield return new LintIssue
                {
                    Severity   = LintSeverity.Warning,
                    RuleId     = RuleId,
                    TrackIndex = track.TrackIndex,
                    Message    = $"Track {track.TrackIndex} (S{track.Season:D2}E{string.Join("E", track.Episodes.Select(e => e.ToString("D2")))}): " +
                                 $"duration {track.Duration.Value.TotalMinutes:F0} min differs significantly from expected " +
                                 $"{expected.TotalMinutes:F0} min ({diffRatio * 100:F0}% deviation). Verify episode assignment."
                };
            }
        }
    }
}
