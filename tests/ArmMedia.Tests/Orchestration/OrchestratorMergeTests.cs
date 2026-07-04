using ArmMedia.Core.Abstractions;
using ArmMedia.Core.Models;
using ArmMedia.Core.Orchestration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace ArmMedia.Tests.Orchestration;

/// <summary>
/// Tests for the merge logic and provider pipeline in
/// <see cref="EpisodeIdentificationOrchestrator"/>.
/// </summary>
public sealed class OrchestratorMergeTests
{
    private static EpisodeIdentificationOptions DefaultOptions() => new()
    {
        ProviderOrder                    = ["ProviderA", "ProviderB"],
        ShortCircuitOnDefinitive         = true,
        RuntimeToleranceSeconds          = 180,
        MultiPartDurationToleranceSeconds = 300,
        ExtraMaxDurationSeconds          = 600
    };

    private static DiscContext MakeContext(int trackCount = 3, int season = 1) => new()
    {
        DiscId      = "TEST_DISC",
        SeriesTitle = "Test Series",
        Season      = season,
        Tracks      = Enumerable.Range(1, trackCount).Select(i => new TrackContext
        {
            TrackIndex  = i,
            Duration    = TimeSpan.FromMinutes(45),
            SizeBytes   = 1_000_000_000L
        }).ToList()
    };

    private static EpisodeIdentificationOrchestrator MakeOrchestrator(
        IEnumerable<IEpisodeIdentificationProvider> providers,
        EpisodeIdentificationOptions? opts = null)
    {
        var options = Options.Create(opts ?? DefaultOptions());
        return new EpisodeIdentificationOrchestrator(
            providers,
            options,
            NullLoggerFactory.Instance);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // TC-01: DiscDb definitive short-circuit
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WhenDiscDbReturnsDefinitive_OtherProvidersAreNotCalled()
    {
        var discDbResults = new[]
        {
            new ProviderResult { TrackIndex = 1, Season = 1, Episodes = [1], Confidence = Confidence.Definitive, ProviderName = "DiscDb" },
            new ProviderResult { TrackIndex = 2, Season = 1, Episodes = [2], Confidence = Confidence.Definitive, ProviderName = "DiscDb" },
            new ProviderResult { TrackIndex = 3, Season = 1, Episodes = [3], Confidence = Confidence.Definitive, ProviderName = "DiscDb" }
        };

        var discDbProvider = new Mock<IEpisodeIdentificationProvider>();
        discDbProvider.Setup(p => p.ProviderName).Returns("ProviderA");
        discDbProvider.Setup(p => p.IdentifyAsync(It.IsAny<DiscContext>(), It.IsAny<CancellationToken>()))
                      .ReturnsAsync(discDbResults);

        var secondProvider = new Mock<IEpisodeIdentificationProvider>();
        secondProvider.Setup(p => p.ProviderName).Returns("ProviderB");
        secondProvider.Setup(p => p.IdentifyAsync(It.IsAny<DiscContext>(), It.IsAny<CancellationToken>()))
                      .ReturnsAsync([]);

        var orchestrator = MakeOrchestrator([discDbProvider.Object, secondProvider.Object]);
        var map = await orchestrator.IdentifyAsync(MakeContext());

        secondProvider.Verify(p => p.IdentifyAsync(It.IsAny<DiscContext>(), It.IsAny<CancellationToken>()),
            Times.Never, "Second provider should not be called when first returns Definitive results.");
        Assert.All(map.Tracks, t => Assert.Equal("DiscDb", t.WinningProvider));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // TC-02: Higher confidence wins per track
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WhenProviderConflict_HigherConfidenceWins()
    {
        var providerA = new Mock<IEpisodeIdentificationProvider>();
        providerA.Setup(p => p.ProviderName).Returns("ProviderA");
        providerA.Setup(p => p.IdentifyAsync(It.IsAny<DiscContext>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync([new ProviderResult { TrackIndex = 1, Season = 1, Episodes = [3], Confidence = Confidence.High,   ProviderName = "ProviderA" }]);

        var providerB = new Mock<IEpisodeIdentificationProvider>();
        providerB.Setup(p => p.ProviderName).Returns("ProviderB");
        providerB.Setup(p => p.IdentifyAsync(It.IsAny<DiscContext>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync([new ProviderResult { TrackIndex = 1, Season = 1, Episodes = [4], Confidence = Confidence.Medium, ProviderName = "ProviderB" }]);

        var opts = DefaultOptions();
        opts.ShortCircuitOnDefinitive = false; // run both providers
        var orchestrator = MakeOrchestrator([providerA.Object, providerB.Object], opts);
        var map = await orchestrator.IdentifyAsync(MakeContext(1));

        var track = map.Tracks.Single(t => t.TrackIndex == 1);
        Assert.Equal([3], track.Episodes);
        Assert.Equal("ProviderA", track.WinningProvider);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // TC-03: Positional fallback fills unresolved tracks
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WhenNoProviderResolvesTrack_PositionalFallbackIsApplied()
    {
        var provider = new Mock<IEpisodeIdentificationProvider>();
        provider.Setup(p => p.ProviderName).Returns("ProviderA");
        provider.Setup(p => p.IdentifyAsync(It.IsAny<DiscContext>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([]); // no results

        var orchestrator = MakeOrchestrator([provider.Object]);
        var ctx = MakeContext(3);
        var map = await orchestrator.IdentifyAsync(ctx);

        Assert.Equal(3, map.Tracks.Count);
        Assert.All(map.Tracks, t =>
        {
            Assert.Equal(Confidence.Low, t.Confidence);
            Assert.Equal("PositionalFallback", t.WinningProvider);
        });
    }

    // ─────────────────────────────────────────────────────────────────────────
    // TC-04: Provider exception is swallowed and pipeline continues
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WhenProviderThrows_PipelineContinuesWithNextProvider()
    {
        var badProvider = new Mock<IEpisodeIdentificationProvider>();
        badProvider.Setup(p => p.ProviderName).Returns("ProviderA");
        badProvider.Setup(p => p.IdentifyAsync(It.IsAny<DiscContext>(), It.IsAny<CancellationToken>()))
                   .ThrowsAsync(new InvalidOperationException("Simulated provider failure"));

        var goodProvider = new Mock<IEpisodeIdentificationProvider>();
        goodProvider.Setup(p => p.ProviderName).Returns("ProviderB");
        goodProvider.Setup(p => p.IdentifyAsync(It.IsAny<DiscContext>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync([new ProviderResult { TrackIndex = 1, Season = 1, Episodes = [1], Confidence = Confidence.High, ProviderName = "ProviderB" }]);

        var opts = DefaultOptions();
        opts.ShortCircuitOnDefinitive = false;
        var orchestrator = MakeOrchestrator([badProvider.Object, goodProvider.Object], opts);

        // Should not throw
        var map = await orchestrator.IdentifyAsync(MakeContext(1));
        Assert.Equal("ProviderB", map.Tracks.First().WinningProvider);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // TC-05: EpisodeMap contains correct series title and season
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EpisodeMap_HasCorrectSeriesTitleAndSeason()
    {
        var provider = new Mock<IEpisodeIdentificationProvider>();
        provider.Setup(p => p.ProviderName).Returns("ProviderA");
        provider.Setup(p => p.IdentifyAsync(It.IsAny<DiscContext>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([]);

        var orchestrator = MakeOrchestrator([provider.Object]);
        var ctx = MakeContext(1, season: 2);
        var map = await orchestrator.IdentifyAsync(ctx);

        Assert.Equal("Test Series", map.SeriesTitle);
        Assert.Equal(2, map.Season);
    }
}
