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
        // Two short tracks (< 15 min each) with similar duration, assigned E03 and E04 by provider
        // These represent a single episode split across two tracks (e.g. a 17-min episode as 8+9 min)
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
                new TrackContext { TrackIndex = 1, Duration = TimeSpan.FromMinutes(11), SizeBytes = 800_000_000 },
                new TrackContext { TrackIndex = 2, Duration = TimeSpan.FromMinutes(12), SizeBytes = 820_000_000 }
            ]
        };

        var orchestrator = MakeOrchestrator([provider.Object]);
        var map = await orchestrator.IdentifyAsync(ctx);

        // Expect a single merged track
        var merged = Assert.Single(map.Tracks, t => !t.IsExtra);
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
                new TrackContext { TrackIndex = 1, Duration = TimeSpan.FromMinutes(8), SizeBytes = 800_000_000 },
                new TrackContext { TrackIndex = 2, Duration = TimeSpan.FromMinutes(8), SizeBytes = 800_000_000 }
            ]
        };

        var orchestrator = MakeOrchestrator([provider.Object]);
        var map = await orchestrator.IdentifyAsync(ctx);

        Assert.Equal(2, map.Tracks.Count);
        Assert.All(map.Tracks, t => Assert.False(t.IsMultiPart));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // TC-MP-04: Normal-length episodes are NOT merged (regression: HIMYM S3D2)
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NormalLengthEpisodes_AreNotMerged()
    {
        // Seven ~21-min sitcom episodes on a disc — should NEVER be merged.
        // This is a regression test for the HIMYM Season 3 Disc 2 bug where
        // consecutive 20-min episodes with similar durations and consecutive
        // episode numbers were incorrectly merged by multi-part detection.
        var provider = new Mock<IEpisodeIdentificationProvider>();
        provider.Setup(p => p.ProviderName).Returns("ProviderA");
        provider.Setup(p => p.IdentifyAsync(It.IsAny<DiscContext>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([
                    new ProviderResult { TrackIndex = 0, Season = 3, Episodes = [1], Confidence = Confidence.High, ProviderName = "ProviderA" },
                    new ProviderResult { TrackIndex = 1, Season = 3, Episodes = [2], Confidence = Confidence.High, ProviderName = "ProviderA" },
                    new ProviderResult { TrackIndex = 2, Season = 3, Episodes = [3], Confidence = Confidence.High, ProviderName = "ProviderA" },
                    new ProviderResult { TrackIndex = 3, Season = 3, Episodes = [4], Confidence = Confidence.High, ProviderName = "ProviderA" },
                    new ProviderResult { TrackIndex = 4, Season = 3, Episodes = [5], Confidence = Confidence.High, ProviderName = "ProviderA" },
                    new ProviderResult { TrackIndex = 5, Season = 3, Episodes = [6], Confidence = Confidence.High, ProviderName = "ProviderA" },
                    new ProviderResult { TrackIndex = 6, Season = 3, Episodes = [7], Confidence = Confidence.High, ProviderName = "ProviderA" }
                ]);

        var ctx = new Core.Models.DiscContext
        {
            DiscId      = "DISC_HIMYM_S3D2",
            SeriesTitle = "How I Met Your Mother",
            Season      = 3,
            Tracks      =
            [
                new TrackContext { TrackIndex = 0, Duration = TimeSpan.FromMinutes(20).Add(TimeSpan.FromSeconds(58)), SizeBytes = 400_000_000 },
                new TrackContext { TrackIndex = 1, Duration = TimeSpan.FromMinutes(21).Add(TimeSpan.FromSeconds(1)),  SizeBytes = 400_000_000 },
                new TrackContext { TrackIndex = 2, Duration = TimeSpan.FromMinutes(21).Add(TimeSpan.FromSeconds(34)), SizeBytes = 400_000_000 },
                new TrackContext { TrackIndex = 3, Duration = TimeSpan.FromMinutes(21).Add(TimeSpan.FromSeconds(41)), SizeBytes = 400_000_000 },
                new TrackContext { TrackIndex = 4, Duration = TimeSpan.FromMinutes(21).Add(TimeSpan.FromSeconds(22)), SizeBytes = 400_000_000 },
                new TrackContext { TrackIndex = 5, Duration = TimeSpan.FromMinutes(21).Add(TimeSpan.FromSeconds(14)), SizeBytes = 400_000_000 },
                new TrackContext { TrackIndex = 6, Duration = TimeSpan.FromMinutes(21).Add(TimeSpan.FromSeconds(42)), SizeBytes = 400_000_000 }
            ]
        };

        var orchestrator = MakeOrchestrator([provider.Object]);
        var map = await orchestrator.IdentifyAsync(ctx);

        // All 7 tracks must remain separate — no merging
        Assert.Equal(7, map.Tracks.Count);
        Assert.All(map.Tracks, t => Assert.False(t.IsMultiPart));

        // Each track should have exactly one episode
        for (int i = 0; i < 7; i++)
        {
            var track = map.Tracks.First(t => t.TrackIndex == i);
            Assert.Single(track.Episodes);
            Assert.Equal(i + 1, track.Episodes[0]);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // TC-MP-05: Short tracks exceed max part duration threshold are NOT merged
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TracksExceedingMaxPartDuration_NotMerged()
    {
        // Two tracks just over the 15-min threshold should not be merged
        var provider = new Mock<IEpisodeIdentificationProvider>();
        provider.Setup(p => p.ProviderName).Returns("ProviderA");
        provider.Setup(p => p.IdentifyAsync(It.IsAny<DiscContext>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([
                    new ProviderResult { TrackIndex = 1, Season = 1, Episodes = [1], Confidence = Confidence.Medium, ProviderName = "ProviderA" },
                    new ProviderResult { TrackIndex = 2, Season = 1, Episodes = [2], Confidence = Confidence.Medium, ProviderName = "ProviderA" }
                ]);

        var ctx = new Core.Models.DiscContext
        {
            DiscId = "DISC_MAXDUR", SeriesTitle = "Test Series", Season = 1,
            Tracks =
            [
                new TrackContext { TrackIndex = 1, Duration = TimeSpan.FromMinutes(16), SizeBytes = 300_000_000 },
                new TrackContext { TrackIndex = 2, Duration = TimeSpan.FromMinutes(16), SizeBytes = 300_000_000 }
            ]
        };

        var orchestrator = MakeOrchestrator([provider.Object]);
        var map = await orchestrator.IdentifyAsync(ctx);

        Assert.Equal(2, map.Tracks.Count);
        Assert.All(map.Tracks, t => Assert.False(t.IsMultiPart));
    }
}
