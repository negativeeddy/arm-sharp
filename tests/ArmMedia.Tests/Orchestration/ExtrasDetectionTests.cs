using ArmMedia.Core.Abstractions;
using ArmMedia.Core.Models;
using ArmMedia.Core.Orchestration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace ArmMedia.Tests.Orchestration;

/// <summary>
/// Tests for extras / bonus feature detection in <see cref="EpisodeIdentificationOrchestrator"/>.
/// </summary>
public sealed class ExtrasDetectionTests
{
    private static EpisodeIdentificationOptions DefaultOptions(int extraMaxSec = 600) => new()
    {
        ProviderOrder           = ["ProviderA"],
        ShortCircuitOnDefinitive = false,
        ExtraMaxDurationSeconds  = extraMaxSec
    };

    private static EpisodeIdentificationOrchestrator MakeOrchestrator(
        IEnumerable<IEpisodeIdentificationProvider> providers,
        EpisodeIdentificationOptions? opts = null)
        => new(providers, Options.Create(opts ?? DefaultOptions()),
               NullLoggerFactory.Instance);

    // ─────────────────────────────────────────────────────────────────────────
    // TC-EX-01: Short track with no episode match → IsExtra = true
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TrackShorterThanThreshold_IsClassifiedAsExtra()
    {
        var provider = new Mock<IEpisodeIdentificationProvider>();
        provider.Setup(p => p.ProviderName).Returns("ProviderA");
        provider.Setup(p => p.IdentifyAsync(It.IsAny<DiscContext>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([
                    new ProviderResult
                    {
                        TrackIndex   = 1,
                        Season       = 1,
                        Episodes     = [1],
                        Confidence   = Confidence.High,
                        ProviderName = "ProviderA",
                        IsExtra      = false
                    }
                ]);

        var ctx = new DiscContext
        {
            DiscId = "DISC_EXTRA", SeriesTitle = "Test Series", Season = 1,
            Tracks =
            [
                // Short track — under the 600 s threshold
                new TrackContext { TrackIndex = 1, Duration = TimeSpan.FromSeconds(300), SizeBytes = 50_000_000 }
            ]
        };

        var orchestrator = MakeOrchestrator([provider.Object]);
        var map = await orchestrator.IdentifyAsync(ctx);

        var track = map.Tracks.Single();
        Assert.True(track.IsExtra);
        Assert.Equal(0, track.Season);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // TC-EX-02: Provider sets Season=0 → IsExtra = true regardless of duration
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ProviderSeasonZero_IsAlwaysClassifiedAsExtra()
    {
        var provider = new Mock<IEpisodeIdentificationProvider>();
        provider.Setup(p => p.ProviderName).Returns("ProviderA");
        provider.Setup(p => p.IdentifyAsync(It.IsAny<DiscContext>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([
                    new ProviderResult
                    {
                        TrackIndex   = 1,
                        Season       = 0,   // explicitly season 0
                        Episodes     = [0],
                        Title        = "Behind the Scenes",
                        Confidence   = Confidence.High,
                        ProviderName = "ProviderA"
                    }
                ]);

        var ctx = new DiscContext
        {
            DiscId = "DISC_S0", SeriesTitle = "Test Series", Season = 1,
            Tracks =
            [
                // Long track (30 min) — but season 0 forces extra classification
                new TrackContext { TrackIndex = 1, Duration = TimeSpan.FromMinutes(30), SizeBytes = 500_000_000 }
            ]
        };

        var orchestrator = MakeOrchestrator([provider.Object]);
        var map = await orchestrator.IdentifyAsync(ctx);

        var track = map.Tracks.Single();
        Assert.True(track.IsExtra);
        Assert.Equal(0, track.Season);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // TC-EX-03: Normal-length episode is NOT classified as extra
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NormalDurationEpisode_IsNotClassifiedAsExtra()
    {
        var provider = new Mock<IEpisodeIdentificationProvider>();
        provider.Setup(p => p.ProviderName).Returns("ProviderA");
        provider.Setup(p => p.IdentifyAsync(It.IsAny<DiscContext>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([
                    new ProviderResult
                    {
                        TrackIndex   = 1,
                        Season       = 1,
                        Episodes     = [1],
                        Confidence   = Confidence.High,
                        ProviderName = "ProviderA"
                    }
                ]);

        var ctx = new DiscContext
        {
            DiscId = "DISC_NORMAL", SeriesTitle = "Test Series", Season = 1,
            Tracks = [new TrackContext { TrackIndex = 1, Duration = TimeSpan.FromMinutes(45), SizeBytes = 900_000_000 }]
        };

        var orchestrator = MakeOrchestrator([provider.Object]);
        var map = await orchestrator.IdentifyAsync(ctx);

        Assert.False(map.Tracks.Single().IsExtra);
        Assert.Equal(1, map.Tracks.Single().Season);
    }
}
