using ArmMedia.Core.Models;
using ArmMedia.Linting;
using ArmMedia.Linting.Models;
using ArmMedia.Linting.Rules;
using Xunit;

namespace ArmMedia.Tests.Linting;

/// <summary>
/// Unit tests for individual lint rules.
/// </summary>
public sealed class LintRuleTests
{
    private static LintOptions DefaultLintOptions() => new() { FailOnError = true, FailOnWarning = false };

    private static EpisodeMap MakeMap(IReadOnlyList<MappedTrack> tracks, int season = 1) => new()
    {
        SeriesTitle = "Test Series",
        Season      = season,
        Tracks      = tracks
    };

    private static MappedTrack MakeTrack(
        int trackIndex, int season, int[] episodes,
        bool isExtra = false,
        Confidence confidence = Confidence.High,
        string provider = "DiscDb") => new()
    {
        TrackIndex      = trackIndex,
        Season          = season,
        Episodes        = episodes,
        IsExtra         = isExtra,
        WinningProvider = provider,
        Confidence      = confidence
    };

    // ─────────────────────────────────────────────────────────────────────────
    // TV001 — Duplicate episode
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TV001_DuplicateEpisode_ReturnsError()
    {
        var tracks = new[]
        {
            MakeTrack(1, 1, [5]),
            MakeTrack(2, 1, [5])   // duplicate S01E05
        };
        var rule   = new DuplicateEpisodeLintRule();
        var issues = rule.Evaluate(MakeMap(tracks), DefaultLintOptions()).ToList();

        Assert.Single(issues);
        Assert.Equal("TV001", issues[0].RuleId);
        Assert.Equal(LintSeverity.Error, issues[0].Severity);
    }

    [Fact]
    public void TV001_NoDuplicates_ReturnsNoIssues()
    {
        var tracks = new[]
        {
            MakeTrack(1, 1, [1]),
            MakeTrack(2, 1, [2])
        };
        var rule   = new DuplicateEpisodeLintRule();
        var issues = rule.Evaluate(MakeMap(tracks), DefaultLintOptions()).ToList();

        Assert.Empty(issues);
    }

    [Fact]
    public void TV001_ExtrasIgnored_NoDuplicate()
    {
        // Two extras both assigned episode [0] should not trigger TV001
        var tracks = new[]
        {
            MakeTrack(1, 0, [0], isExtra: true),
            MakeTrack(2, 0, [0], isExtra: true)
        };
        var rule   = new DuplicateEpisodeLintRule();
        var issues = rule.Evaluate(MakeMap(tracks), DefaultLintOptions()).ToList();

        Assert.Empty(issues);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // TV002 — Episode sequence gap
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TV002_SequenceGap_ReturnsWarning()
    {
        // E01, E02, E04 (gap at E03)
        var tracks = new[]
        {
            MakeTrack(1, 1, [1]),
            MakeTrack(2, 1, [2]),
            MakeTrack(3, 1, [4])
        };
        var rule   = new EpisodeGapLintRule();
        var issues = rule.Evaluate(MakeMap(tracks), DefaultLintOptions()).ToList();

        Assert.Single(issues);
        Assert.Equal("TV002", issues[0].RuleId);
        Assert.Equal(LintSeverity.Warning, issues[0].Severity);
        Assert.Contains("E03", issues[0].Message);
    }

    [Fact]
    public void TV002_NoGap_ReturnsNoIssues()
    {
        var tracks = new[]
        {
            MakeTrack(1, 1, [1]),
            MakeTrack(2, 1, [2]),
            MakeTrack(3, 1, [3])
        };
        var rule   = new EpisodeGapLintRule();
        var issues = rule.Evaluate(MakeMap(tracks), DefaultLintOptions()).ToList();

        Assert.Empty(issues);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // TV004 — Low-confidence positional fallback
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TV004_LowConfidenceTrack_ReturnsInfo()
    {
        var tracks = new[]
        {
            MakeTrack(1, 1, [1], confidence: Confidence.Low, provider: "PositionalFallback")
        };
        var rule   = new LowConfidenceLintRule();
        var issues = rule.Evaluate(MakeMap(tracks), DefaultLintOptions()).ToList();

        Assert.Single(issues);
        Assert.Equal("TV004", issues[0].RuleId);
        Assert.Equal(LintSeverity.Info, issues[0].Severity);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // TV006 — Season mismatch
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TV006_SeasonMismatch_ReturnsError()
    {
        // Disc is season 1 but a track claims season 2
        var tracks = new[]
        {
            MakeTrack(1, 1, [1]),
            MakeTrack(2, 2, [1])  // wrong season
        };
        var rule   = new SeasonMismatchLintRule();
        var issues = rule.Evaluate(MakeMap(tracks, season: 1), DefaultLintOptions()).ToList();

        Assert.Single(issues);
        Assert.Equal("TV006", issues[0].RuleId);
        Assert.Equal(LintSeverity.Error, issues[0].Severity);
    }

    [Fact]
    public void TV006_ExtrasSeasonZeroIgnored_NoError()
    {
        // Extras have season 0; they should not trigger TV006
        var tracks = new[]
        {
            MakeTrack(1, 1, [1]),
            MakeTrack(2, 0, [0], isExtra: true)
        };
        var rule   = new SeasonMismatchLintRule();
        var issues = rule.Evaluate(MakeMap(tracks, season: 1), DefaultLintOptions()).ToList();

        Assert.Empty(issues);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // TV003 — Runtime mismatch
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TV003_NoExpectedRuntimeConfigured_ReturnsNoIssues()
    {
        var track  = MakeTrack(1, 1, [1], confidence: Confidence.High);
        var rule   = new RuntimeMismatchLintRule();
        var issues = rule.Evaluate(MakeMap([track]), new LintOptions()).ToList();

        Assert.Empty(issues);
    }

    [Fact]
    public void TV003_DurationWithinTolerance_ReturnsNoIssues()
    {
        var opts = new LintOptions { ExpectedEpisodeDurationSeconds = 2700 }; // 45 min
        var track = MakeTrack(1, 1, [1], confidence: Confidence.High);

        // Create a map with duration via reflection or constructor; MappedTrack
        // doesn't have Duration set in MakeTrack helper, so we need to set it manually.
        var trackWithDuration = new MappedTrack
        {
            TrackIndex      = 1,
            Season          = 1,
            Episodes        = [1],
            Title           = "Normal Episode",
            IsExtra         = false,
            WinningProvider = "Test",
            Confidence      = Confidence.High,
            Duration        = TimeSpan.FromMinutes(43) // 43 min, ~4.4% off — within 25%
        };

        var rule   = new RuntimeMismatchLintRule();
        var issues = rule.Evaluate(MakeMap([trackWithDuration]), opts).ToList();

        Assert.Empty(issues);
    }

    [Fact]
    public void TV003_DurationFarFromExpected_ReturnsWarning()
    {
        var opts = new LintOptions { ExpectedEpisodeDurationSeconds = 2700 }; // 45 min
        var trackWithDuration = new MappedTrack
        {
            TrackIndex      = 1,
            Season          = 1,
            Episodes        = [1],
            Title           = "Too Short",
            IsExtra         = false,
            WinningProvider = "Test",
            Confidence      = Confidence.High,
            Duration        = TimeSpan.FromMinutes(15) // 15 min, ~67% off — exceeds 25%
        };

        var rule   = new RuntimeMismatchLintRule();
        var issues = rule.Evaluate(MakeMap([trackWithDuration]), opts).ToList();

        Assert.Single(issues);
        Assert.Equal("TV003", issues[0].RuleId);
        Assert.Equal(LintSeverity.Warning, issues[0].Severity);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // TV005 — Multi-part duration mismatch
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TV005_SingleEpisodeTrack_ReturnsNoIssues()
    {
        var opts   = new LintOptions { ExpectedEpisodeDurationSeconds = 2700 };
        var track  = MakeTrack(1, 1, [1], confidence: Confidence.High);
        var rule   = new MultiPartDurationMismatchLintRule();
        var issues = rule.Evaluate(MakeMap([track]), opts).ToList();

        Assert.Empty(issues);
    }

    [Fact]
    public void TV005_MultiPartReasonableDuration_ReturnsNoIssues()
    {
        var opts = new LintOptions { ExpectedEpisodeDurationSeconds = 2700 }; // 45 min
        var track = new MappedTrack
        {
            TrackIndex      = 1,
            Season          = 1,
            Episodes        = [3, 4], // multi-part
            Title           = "Two Parter",
            IsExtra         = false,
            WinningProvider = "Test",
            Confidence      = Confidence.High,
            Duration        = TimeSpan.FromMinutes(88) // ~88 min for 2 eps — close to 90
        };

        var rule   = new MultiPartDurationMismatchLintRule();
        var issues = rule.Evaluate(MakeMap([track]), opts).ToList();

        Assert.Empty(issues);
    }

    [Fact]
    public void TV005_MultiPartTooShort_ReturnsWarning()
    {
        var opts = new LintOptions { ExpectedEpisodeDurationSeconds = 2700 }; // 45 min
        var track = new MappedTrack
        {
            TrackIndex      = 1,
            Season          = 1,
            Episodes        = [3, 4], // multi-part
            Title           = "Suspiciously Short",
            IsExtra         = false,
            WinningProvider = "Test",
            Confidence      = Confidence.High,
            Duration        = TimeSpan.FromMinutes(15) // 15 min for 2 eps — way too short
        };

        var rule   = new MultiPartDurationMismatchLintRule();
        var issues = rule.Evaluate(MakeMap([track]), opts).ToList();

        Assert.Single(issues);
        Assert.Equal("TV005", issues[0].RuleId);
        Assert.Equal(LintSeverity.Warning, issues[0].Severity);
    }
}
