using ArmMedia.Core.Abstractions;
using ArmMedia.Core.Models;
using ArmMedia.Core.Orchestration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace ArmMedia.Tests.Orchestration;

/// <summary>
/// Tests for multi-part episode detection in <see cref="EpisodeIdentificationOrchestrator"/>.
/// </summary>
public sealed class MultiPartDetectionTests
{
    private static EpisodeIdentificationOptions DefaultOptions() => new()
    {
        ProviderOrder                    = ["ProviderA"],
        ShortCircuitOnDefinitive         = false,
        MultiPartDurationToleranceSeconds = 300,
        ExtraMaxDurationSeconds          = 600
    };

    private static EpisodeIdentificationOrchestrator MakeOrchestrator(
        IEnumerable<IEpisodeIdentificationProvider> providers,
        EpisodeIdentificationOptions? opts = null)
    {
        return new EpisodeIdentificationOrchestrator(
            providers,
            Options.Create(opts ?? DefaultOptions()),
            NullLogger<EpisodeIdentificationOrchestrator>.Instance);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // TC-MP-01: Consecutive same-duration tracks are merged
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ConsecutiveEqualDurationTracks_AreMergedAsMultiPart()
    {
        // Two tracks with the same ~45 min duration, assigned E03 and E04 by provider
        var provider = new Mock<IEpisodeIdentificationProvider>();
        provider.Setup(p => p.ProviderName).Returns("ProviderA");
        provider.Setup(p => p.IdentifyAsync(It.IsAny<DiscContext>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([
                    new ProviderResult { TrackIndex = 1, Season = 1, Episodes = [3], Confidence = Confidence.Medium, ProviderName = "ProviderA", Title = "Part 1" },
                    new ProviderResult { TrackIndex = 2, Season = 1, Episodes = [4], Confidence = Confidence.Medium, ProviderName = "ProviderA", Title = "Part 2" }
                ]);

        var ctx = new Core.Models.DiscContext
        {
            DiscId      = "DISC_MP",
            SeriesTitle = "Test Series",
            Season      = 1,
            Tracks      =
            [
                new TrackContext { TrackIndex = 1, Duration = TimeSpan.FromMinutes(44), SizeBytes = 800_000_000 },
                new TrackContext { TrackIndex = 2, Duration = TimeSpan.FromMinutes(46), SizeBytes = 820_000_000 }
            ]
        };

        var orchestrator = MakeOrchestrator([provider.Object]);
        var map = await orchestrator.IdentifyAsync(ctx);

        // Expect a single merged track
        var merged = Assert.Single(map.Tracks.Where(t => !t.IsExtra));
        Assert.True(merged.IsMultiPart);
        Assert.Equal([3, 4], merged.Episodes);
        Assert.Equal(1, merged.TrackIndex); // winning track index is the first
    }

    // ─────────────────────────────────────────────────────────────────────────
    // TC-MP-02: Tracks with large duration delta are NOT merged
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TracksBeyondDurationTolerance_AreNotMerged()
    {
        var provider = new Mock<IEpisodeIdentificationProvider>();
        provider.Setup(p => p.ProviderName).Returns("ProviderA");
        provider.Setup(p => p.IdentifyAsync(It.IsAny<DiscContext>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([
                    new ProviderResult { TrackIndex = 1, Season = 1, Episodes = [1], Confidence = Confidence.Medium, ProviderName = "ProviderA" },
                    new ProviderResult { TrackIndex = 2, Season = 1, Episodes = [2], Confidence = Confidence.Medium, ProviderName = "ProviderA" }
                ]);

        var ctx = new Core.Models.DiscContext
        {
            DiscId      = "DISC_NO_MERGE",
            SeriesTitle = "Test Series",
            Season      = 1,
            Tracks      =
            [
                new TrackContext { TrackIndex = 1, Duration = TimeSpan.FromMinutes(45), SizeBytes = 800_000_000 },
                new TrackContext { TrackIndex = 2, Duration = TimeSpan.FromMinutes(20), SizeBytes = 400_000_000 }  // too short
            ]
        };

        var opts = DefaultOptions();
        opts.MultiPartDurationToleranceSeconds = 300; // 5 min tolerance

        var orchestrator = MakeOrchestrator([provider.Object], opts);
        var map = await orchestrator.IdentifyAsync(ctx);

        Assert.Equal(2, map.Tracks.Count);
        Assert.All(map.Tracks, t => Assert.False(t.IsMultiPart));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // TC-MP-03: Low-confidence tracks are not candidates for multi-part merge
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task LowConfidenceTracks_NotMergedAsMultiPart()
    {
        var provider = new Mock<IEpisodeIdentificationProvider>();
        provider.Setup(p => p.ProviderName).Returns("ProviderA");
        provider.Setup(p => p.IdentifyAsync(It.IsAny<DiscContext>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([
                    new ProviderResult { TrackIndex = 1, Season = 1, Episodes = [1], Confidence = Confidence.Low, ProviderName = "ProviderA" },
                    new ProviderResult { TrackIndex = 2, Season = 1, Episodes = [2], Confidence = Confidence.Low, ProviderName = "ProviderA" }
                ]);

        var ctx = new Core.Models.DiscContext
        {
            DiscId = "DISC_LOWCONF", SeriesTitle = "Test Series", Season = 1,
            Tracks =
            [
                new TrackContext { TrackIndex = 1, Duration = TimeSpan.FromMinutes(45), SizeBytes = 800_000_000 },
                new TrackContext { TrackIndex = 2, Duration = TimeSpan.FromMinutes(45), SizeBytes = 800_000_000 }
            ]
        };

        var orchestrator = MakeOrchestrator([provider.Object]);
        var map = await orchestrator.IdentifyAsync(ctx);

        Assert.Equal(2, map.Tracks.Count);
        Assert.All(map.Tracks, t => Assert.False(t.IsMultiPart));
    }
}
