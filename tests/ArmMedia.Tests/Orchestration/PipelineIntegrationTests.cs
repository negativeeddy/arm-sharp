using ArmMedia.Core.Abstractions;
using ArmMedia.Core.Models;
using ArmMedia.Core.Orchestration;
using ArmMedia.Linting;
using ArmMedia.Linting.Models;
using ArmMedia.Linting.Rules;
using ArmMedia.Naming;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace ArmMedia.Tests.Orchestration;

/// <summary>
/// End-to-end integration test that exercises the full pipeline:
/// orchestrator → linting → naming.
/// </summary>
public sealed class PipelineIntegrationTests
{
    private static DiscContext MakeCosmicFrontierContext() => new()
    {
        DiscId      = "COSMIC_S01",
        SeriesTitle = "Cosmic Frontier",
        Season      = 1,
        Tracks =
        [
            new TrackContext { TrackIndex = 1, Duration = TimeSpan.FromMinutes(44), SizeBytes = 900_000_000 },
            new TrackContext { TrackIndex = 2, Duration = TimeSpan.FromMinutes(37), SizeBytes = 750_000_000 }, // different enough from track 1 to avoid false merge
            new TrackContext { TrackIndex = 3, Duration = TimeSpan.FromMinutes(44), SizeBytes = 880_000_000 },
            new TrackContext { TrackIndex = 4, Duration = TimeSpan.FromMinutes(46), SizeBytes = 910_000_000 }, // similar to track 3 → will merge
            new TrackContext { TrackIndex = 5, Duration = TimeSpan.FromMinutes(45), SizeBytes = 890_000_000 },
            new TrackContext { TrackIndex = 6, Duration = TimeSpan.FromSeconds(300), SizeBytes = 50_000_000 },
            new TrackContext { TrackIndex = 7, Duration = TimeSpan.FromSeconds(480), SizeBytes = 80_000_000 }
        ]
    };

    private static EpisodeIdentificationOptions MakeOptions() => new()
    {
        ProviderOrder                    = ["DiscDb", "FileBot", "PositionalFallback"],
        ShortCircuitOnDefinitive         = false, // run all providers
        RuntimeToleranceSeconds          = 180,
        MultiPartDurationToleranceSeconds = 300,
        ExtraMaxDurationSeconds          = 600
    };

    /// <summary>
    /// Full pipeline: provider results → orchestrator merge → lint → name.
    /// Verifies the expected EpisodeMap from the "Cosmic Frontier" sample.
    /// </summary>
    [Fact]
    public async Task FullPipeline_ProducesExpectedEpisodeMap()
    {
        // ── Arrange ───────────────────────────────────────────────────────────
        var ctx = MakeCosmicFrontierContext();

        // DiscDb provider: returns tracks 1, 5, 6, 7 (definitive)
        var discDbProvider = new Mock<IEpisodeIdentificationProvider>();
        discDbProvider.Setup(p => p.ProviderName).Returns("DiscDb");
        discDbProvider.Setup(p => p.IdentifyAsync(It.IsAny<DiscContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new ProviderResult { TrackIndex = 1, Season = 1, Episodes = [1], Title = "Pilot",                Confidence = Confidence.Definitive, ProviderName = "DiscDb" },
                new ProviderResult { TrackIndex = 5, Season = 1, Episodes = [5], Title = "Arrival",              Confidence = Confidence.Definitive, ProviderName = "DiscDb" },
                new ProviderResult { TrackIndex = 6, Season = 0, Episodes = [0], Title = "Making of Cosmic Frontier", IsExtra = true, Confidence = Confidence.Definitive, ProviderName = "DiscDb" },
                new ProviderResult { TrackIndex = 7, Season = 0, Episodes = [0], Title = "Deleted Scenes",            IsExtra = true, Confidence = Confidence.Definitive, ProviderName = "DiscDb" }
            ]);

        // FileBot provider: returns tracks 2, 3 (high), and track 4 (will merge with 3)
        var fileBotProvider = new Mock<IEpisodeIdentificationProvider>();
        fileBotProvider.Setup(p => p.ProviderName).Returns("FileBot");
        fileBotProvider.Setup(p => p.IdentifyAsync(It.IsAny<DiscContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new ProviderResult { TrackIndex = 2, Season = 1, Episodes = [2], Title = "The Setup",            Confidence = Confidence.High, ProviderName = "FileBot" },
                new ProviderResult { TrackIndex = 3, Season = 1, Episodes = [3], Title = "The Long Night (1)",   Confidence = Confidence.High, ProviderName = "FileBot" },
                new ProviderResult { TrackIndex = 4, Season = 1, Episodes = [4], Title = "The Long Night (2)",   Confidence = Confidence.High, ProviderName = "FileBot" }
            ]);

        var options = Options.Create(MakeOptions());
        var orchestrator = new EpisodeIdentificationOrchestrator(
            [discDbProvider.Object, fileBotProvider.Object],
            options,
            NullLoggerFactory.Instance);

        // ── Act: Run identification ──────────────────────────────────────────
        var episodeMap = await orchestrator.IdentifyAsync(ctx);

        // ── Assert: EpisodeMap structure ─────────────────────────────────────
        Assert.Equal("Cosmic Frontier", episodeMap.SeriesTitle);
        Assert.Equal(1, episodeMap.Season);

        // After multi-part merge: tracks 3+4 merged, so we expect 6 mapped tracks
        // (merged track occupies track 3's position)
        Assert.Equal(6, episodeMap.Tracks.Count);

        // Track 1: Pilot (DiscDb, Definitive)
        var t1 = Assert.Single(episodeMap.Tracks, t => t.TrackIndex == 1);
        Assert.Equal([1], t1.Episodes);
        Assert.Equal("Pilot", t1.Title);
        Assert.Equal(Confidence.Definitive, t1.Confidence);
        Assert.Equal("DiscDb", t1.WinningProvider);
        Assert.False(t1.IsExtra);
        Assert.False(t1.IsMultiPart);

        // Track 2: The Setup (FileBot, High)
        var t2 = Assert.Single(episodeMap.Tracks, t => t.TrackIndex == 2);
        Assert.Equal([2], t2.Episodes);
        Assert.Equal("The Setup", t2.Title);
        Assert.Equal(Confidence.High, t2.Confidence);
        Assert.Equal("FileBot", t2.WinningProvider);

        // Track 3: Merged multi-part [3,4] (FileBot, High)
        var t3 = Assert.Single(episodeMap.Tracks, t => t.TrackIndex == 3);
        Assert.Equal([3, 4], t3.Episodes);
        Assert.True(t3.IsMultiPart);
        Assert.Equal("FileBot", t3.WinningProvider);

        // Track 5: Arrival (DiscDb, Definitive) — track 4 was consumed by merge
        var t5 = Assert.Single(episodeMap.Tracks, t => t.TrackIndex == 5);
        Assert.Equal([5], t5.Episodes);
        Assert.Equal("Arrival", t5.Title);
        Assert.Equal(Confidence.Definitive, t5.Confidence);

        // Track 6: Extra (DiscDb, Definitive)
        var t6 = Assert.Single(episodeMap.Tracks, t => t.TrackIndex == 6);
        Assert.True(t6.IsExtra);
        Assert.Equal(0, t6.Season);
        Assert.Equal("Making of Cosmic Frontier", t6.Title);

        // Track 7: Extra (DiscDb, Definitive)
        var t7 = Assert.Single(episodeMap.Tracks, t => t.TrackIndex == 7);
        Assert.True(t7.IsExtra);
        Assert.Equal(0, t7.Season);
        Assert.Equal("Deleted Scenes", t7.Title);

        // ── Act: Lint ────────────────────────────────────────────────────────
        var lintOptions = new LintOptions
        {
            FailOnError = true
            // ExpectedEpisodeDurationSeconds is intentionally not set so TV003
            // and TV005 are skipped (they have dedicated unit tests).
        };
        var lintEngine = new DefaultLintingEngine(
            [new DuplicateEpisodeLintRule(), new EpisodeGapLintRule(),
             new LowConfidenceLintRule(), new RuntimeMismatchLintRule(),
             new MultiPartDurationMismatchLintRule(), new SeasonMismatchLintRule()],
            NullLoggerFactory.Instance);
        var report = lintEngine.Lint(episodeMap, lintOptions);

        // ── Assert: Lint report is clean ─────────────────────────────────────
        Assert.True(report.IsClean, $"Lint should be clean but found issues: {string.Join("; ", report.Issues.Select(i => $"[{i.RuleId}] {i.Message}"))}");

        // ── Act: Naming ──────────────────────────────────────────────────────
        var renamer = new DefaultEpisodeRenamer();
        var namingOpts = NamingOptions.Jellyfin;
        namingOpts.SeriesTitle = "Cosmic Frontier";

        // ── Assert: File names ───────────────────────────────────────────────
        Assert.Equal("Cosmic Frontier - S01E01 - Pilot",                     renamer.Rename(t1, namingOpts));
        Assert.Equal("Cosmic Frontier - S01E02 - The Setup",                 renamer.Rename(t2, namingOpts));
        // Multi-part: S01E03E04 (for [3,4] with MultiPartSep="E")
        var renamedMulti = renamer.Rename(t3, namingOpts);
        Assert.Contains("E03E04", renamedMulti);
        Assert.Contains("Cosmic Frontier", renamedMulti);
        Assert.Equal("Cosmic Frontier - S01E05 - Arrival",                   renamer.Rename(t5, namingOpts));
        Assert.Equal("Cosmic Frontier - S00 - Making of Cosmic Frontier",    renamer.Rename(t6, namingOpts));
        Assert.Equal("Cosmic Frontier - S00 - Deleted Scenes",               renamer.Rename(t7, namingOpts));
    }
}
