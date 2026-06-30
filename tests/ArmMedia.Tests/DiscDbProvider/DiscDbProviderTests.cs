using ArmMedia.Core.Abstractions;
using ArmMedia.Core.Models;
using ArmMedia.DiscDbProvider;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace ArmMedia.Tests.DiscDbProvider;

/// <summary>
/// Tests for <see cref="ArmMedia.DiscDbProvider.DiscDbProvider"/>.
/// </summary>
public sealed class DiscDbProviderTests
{
    private static DiscContext MakeContext(string discId = "DISC_HASH_1234", int trackCount = 3) => new()
    {
        DiscId      = discId,
        SeriesTitle = "Test Series",
        Season      = 1,
        Tracks      = Enumerable.Range(1, trackCount).Select(i => new TrackContext
        {
            TrackIndex  = i,
            Duration    = TimeSpan.FromMinutes(45),
            SizeBytes   = 1_000_000_000L
        }).ToList()
    };

    private static DiscDbLookupResult MakeDiscDbResult()
    {
        return new DiscDbLookupResult
        {
            Title = "Test Series",
            Year  = "2024",
            Tracks =
            [
                new DiscDbLookupTrack
                {
                    TrackIndex  = 1,
                    Title       = "Pilot",
                    Season      = 1,
                    Episode     = 1,
                    ContentType = "MainMovie"
                },
                new DiscDbLookupTrack
                {
                    TrackIndex  = 2,
                    Title       = "The Setup",
                    Season      = 1,
                    Episode     = 2,
                    ContentType = "MainMovie"
                }
            ]
        };
    }

    // ─────────────────────────────────────────────────────────────────────────
    // TC-DD-01: DiscDb record found → returns Definitive results
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WhenDiscDbRecordFound_ReturnsDefinitiveResults()
    {
        var lookupService = new Mock<IDiscDbLookupService>();
        lookupService.Setup(s => s.LookupDiscAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeDiscDbResult());

        var provider = new ArmMedia.DiscDbProvider.DiscDbProvider(
            lookupService.Object,
            NullLogger<ArmMedia.DiscDbProvider.DiscDbProvider>.Instance);

        var results = await provider.IdentifyAsync(MakeContext());

        Assert.NotEmpty(results);
        Assert.All(results, r => Assert.Equal(Confidence.Definitive, r.Confidence));
        Assert.All(results, r => Assert.Equal("DiscDb", r.ProviderName));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // TC-DD-02: No DiscDb record found → returns empty
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WhenNoDiscDbRecord_ReturnsEmpty()
    {
        var lookupService = new Mock<IDiscDbLookupService>();
        lookupService.Setup(s => s.LookupDiscAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DiscDbLookupResult?)null);

        var provider = new ArmMedia.DiscDbProvider.DiscDbProvider(
            lookupService.Object,
            NullLogger<ArmMedia.DiscDbProvider.DiscDbProvider>.Instance);

        var results = await provider.IdentifyAsync(MakeContext());

        Assert.Empty(results);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // TC-DD-03: Provider name is "DiscDb"
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ProviderName_IsDiscDb()
    {
        var lookupService = new Mock<IDiscDbLookupService>();
        var provider = new ArmMedia.DiscDbProvider.DiscDbProvider(
            lookupService.Object,
            NullLogger<ArmMedia.DiscDbProvider.DiscDbProvider>.Instance);

        Assert.Equal("DiscDb", provider.ProviderName);
    }
}
