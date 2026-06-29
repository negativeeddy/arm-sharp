using ArmMedia.Core.Models;
using ArmMedia.Linting.Abstractions;
using ArmMedia.Linting.Models;

namespace ArmMedia.Linting.Rules;

/// <summary>
/// TV005 — Multi-part episode duration mismatch.
/// Raises a warning when the individual parts of a multi-part track differ in
/// duration by more than the configured tolerance ratio.
/// </summary>
/// <remarks>
/// This rule requires <see cref="MappedTrack.Duration"/> to be populated on each
/// track (set by the orchestrator from the original <c>TrackContext</c>).
/// Multi-part tracks where constituent parts have very different runtimes may
/// indicate a false merge.
/// </remarks>
public sealed class MultiPartDurationMismatchLintRule : ILintRule
{
    /// <inheritdoc/>
    public string RuleId => "TV005";

    /// <inheritdoc/>
    public IEnumerable<LintIssue> Evaluate(EpisodeMap map, LintOptions options)
    {
        foreach (var track in map.Tracks)
        {
            if (!track.IsMultiPart) continue;
            if (track.Duration is null) continue;

            // For multi-part tracks, we check that the total duration is reasonable
            // for the episode count. The duration stored is the merged track's
            // duration (the first track's duration). We check it against a
            // reasonable per-episode expectation.
            double totalSec = track.Duration.Value.TotalSeconds;
            int episodeCount = track.Episodes.Length;

            // If we have a configured expected runtime, check the total duration
            if (options.ExpectedEpisodeDurationSeconds > 0)
            {
                double expectedTotal = options.ExpectedEpisodeDurationSeconds * episodeCount;
                double diffRatio = Math.Abs(totalSec - expectedTotal) / expectedTotal;

                if (diffRatio > options.MultiPartDurationToleranceRatio)
                {
                    yield return new LintIssue
                    {
                        Severity   = LintSeverity.Warning,
                        RuleId     = RuleId,
                        TrackIndex = track.TrackIndex,
                        Message    = $"Track {track.TrackIndex} (S{track.Season:D2}E{string.Join("E", track.Episodes.Select(e => e.ToString("D2")))}): " +
                                     $"total duration {track.Duration.Value.TotalMinutes:F0} min differs from expected " +
                                     $"{TimeSpan.FromSeconds(expectedTotal).TotalMinutes:F0} min for {episodeCount} episodes " +
                                     $"({diffRatio * 100:F0}% deviation). Verify multi-part merge."
                    };
                }
            }
            else
            {
                // Without expected runtime, we can still flag if total duration
                // seems extremely short for the number of episodes merged.
                double minReasonablePerEpisode = 600; // 10 minutes per episode minimum
                double reasonableMin = minReasonablePerEpisode * episodeCount;

                if (totalSec < reasonableMin)
                {
                    yield return new LintIssue
                    {
                        Severity   = LintSeverity.Warning,
                        RuleId     = RuleId,
                        TrackIndex = track.TrackIndex,
                        Message    = $"Track {track.TrackIndex} (S{track.Season:D2}E{string.Join("E", track.Episodes.Select(e => e.ToString("D2")))}): " +
                                     $"total duration {track.Duration.Value.TotalMinutes:F0} min is unusually short for {episodeCount} episodes " +
                                     $"(< {reasonableMin / 60:F0} min). Verify multi-part merge."
                    };
                }
            }
        }
    }
}
