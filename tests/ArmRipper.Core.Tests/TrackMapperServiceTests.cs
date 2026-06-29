using System.Text.Json;
using System.Text.Json.Serialization;
using ArmRipper.Core.Infrastructure.Data;
using ArmRipper.Core.Models;
using ArmRipper.Core.Rip;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

namespace ArmRipper.Core.Tests.Rip;

public class TrackMapperServiceTests
{
    private static (TrackMapperService Service, ArmDbContext Db) CreateService()
    {
        var options = new DbContextOptionsBuilder<ArmDbContext>()
            .UseSqlite($"Data Source=file:{Guid.NewGuid():N}?mode=memory&cache=shared")
            .Options;
        var db = new ArmDbContext(options);
        db.Database.OpenConnection();
        db.Database.EnsureCreated();
        var logger = new Mock<ILogger<TrackMapperService>>().Object;
        return (new TrackMapperService(db, logger), db);
    }

    private static async Task<Job> AddJobWithTracksAsync(ArmDbContext db, Job job, params Track[] tracks)
    {
        db.Jobs.Add(job);
        await db.SaveChangesAsync();
        foreach (var t in tracks)
        {
            t.JobId = job.Id;
            db.Tracks.Add(t);
        }
        await db.SaveChangesAsync();
        return job;
    }

    [Fact]
    public async Task MapTracksAsync_ExactIndexMatch_MapsEpisodeData()
    {
        var (service, db) = CreateService();
        var job = new Job { Title = "Test Series", VideoType = "series" };
        var track = new Track
        {
            TrackNumber = "1",
            Length = 45 * 60,
            FileSize = 1_073_741_824,
            FileName = "title_t00.mkv"
        };
        await AddJobWithTracksAsync(db, job, track);

        var discDb = new DiscDbMediaResult
        {
            Id = 1,
            Title = "Test Series",
            Type = "tv",
            Releases =
            [
                new DiscDbRelease
                {
                    Discs =
                    [
                        new DiscDbDisc
                        {
                            Index = 0,
                            Titles =
                            [
                                new DiscDbTitle
                                {
                                    Index = 1,
                                    Duration = 45 * 60,
                                    Size = 1_073_741_824,
                                    Item = new DiscDbItem
                                    {
                                        Title = "Pilot", Season = 1, Episode = 1, Type = "main"
                                    }
                                }
                            ]
                        }
                    ]
                }
            ]
        };

        var confidence = await service.MapTracksAsync(job, discDb, CancellationToken.None);

        Assert.True(confidence > 0.7, $"Expected confidence > 0.7, got {confidence:F2}");
        Assert.Equal(1, track.EpisodeNumber);
        Assert.Equal("Pilot", track.EpisodeTitle);
        Assert.Equal("main", track.ContentType);
        Assert.Equal(1, track.TrackSeasonNumber);
    }

    [Fact]
    public async Task MapTracksAsync_NoDiscDbData_ReturnsZero()
    {
        var (service, db) = CreateService();
        var job = new Job { Title = "Test" };
        db.Jobs.Add(job);
        await db.SaveChangesAsync();

        var confidence = await service.MapTracksAsync(job, null, CancellationToken.None);
        Assert.Equal(0.0, confidence);
    }

    [Fact]
    public async Task MapTracksAsync_NoTracks_ReturnsZero()
    {
        var (service, db) = CreateService();
        var job = new Job { Title = "Test" };
        db.Jobs.Add(job);
        await db.SaveChangesAsync();

        var discDb = new DiscDbMediaResult
        {
            Title = "Test Movie",
            Releases = [new DiscDbRelease { Discs = [new DiscDbDisc { Titles = [new DiscDbTitle { Index = 1 }] }] }]
        };

        var confidence = await service.MapTracksAsync(job, discDb, CancellationToken.None);
        Assert.Equal(0.0, confidence);
    }

    [Fact]
    public async Task MapTracksAsync_TrackIndexMismatchButDurationMatch_MapsWithLowerConfidence()
    {
        var (service, db) = CreateService();
        var job = new Job { Title = "Test Movie", VideoType = "movie" };
        var track = new Track
        {
            TrackNumber = "5", // Different from DiscDb index 2
            Length = 89 * 60,  // Exact duration match (89 min)
            FileName = "title_t05.mkv"
        };
        await AddJobWithTracksAsync(db, job, track);

        var discDb = new DiscDbMediaResult
        {
            Title = "Test Movie",
            Type = "movie",
            Releases =
            [
                new DiscDbRelease
                {
                    Discs =
                    [
                        new DiscDbDisc
                        {
                            Titles =
                            [
                                new DiscDbTitle
                                {
                                    Index = 2,        // Not matching track index 5
                                    Duration = 89 * 60, // Exact duration match
                                    Item = new DiscDbItem { Title = "The Feature", Type = "main" }
                                }
                            ]
                        }
                    ]
                }
            ]
        };

        var confidence = await service.MapTracksAsync(job, discDb, CancellationToken.None);

        // Duration-only match at exactly 0.30 weighted confidence (just at threshold)
        Assert.True(confidence >= 0.3, $"Expected confidence >= 0.3, got {confidence:F2}");
        Assert.Equal("The Feature", track.EpisodeTitle);
    }

    [Fact]
    public async Task MapTracksAsync_NoMatchBelowThreshold_DoesNotMap()
    {
        var (service, db) = CreateService();
        var job = new Job { Title = "Test" };
        var track = new Track { TrackNumber = "99", Length = 99999, FileName = "title_t98.mkv" };
        await AddJobWithTracksAsync(db, job, track);

        var discDb = new DiscDbMediaResult
        {
            Title = "Test",
            Releases =
            [
                new DiscDbRelease
                {
                    Discs =
                    [
                        new DiscDbDisc
                        {
                            Titles =
                            [
                                new DiscDbTitle
                                {
                                    Index = 1, Duration = 60, Size = 1000,
                                    Item = new DiscDbItem { Title = "Episode 1" }
                                }
                            ]
                        }
                    ]
                }
            ]
        };

        var confidence = await service.MapTracksAsync(job, discDb, CancellationToken.None);

        Assert.Equal(0.0, confidence);
        Assert.Null(track.EpisodeNumber);
        Assert.Null(track.EpisodeTitle);
    }

    [Fact]
    public async Task MapTracksAsync_MultipleTracks_MapsAllMatching()
    {
        var (service, db) = CreateService();
        var job = new Job { Title = "TV Show S1", VideoType = "series" };
        var track1 = new Track { TrackNumber = "1", Length = 45 * 60, FileName = "title_t00.mkv" };
        var track2 = new Track { TrackNumber = "2", Length = 46 * 60, FileName = "title_t01.mkv" };
        var track3 = new Track { TrackNumber = "3", Length = 44 * 60, FileName = "title_t02.mkv" };
        await AddJobWithTracksAsync(db, job, track1, track2, track3);

        var discDb = new DiscDbMediaResult
        {
            Title = "TV Show",
            Type = "tv",
            Releases =
            [
                new DiscDbRelease
                {
                    Discs =
                    [
                        new DiscDbDisc
                        {
                            Titles =
                            [
                                new DiscDbTitle
                                {
                                    Index = 1, Duration = 45 * 60,
                                    Item = new DiscDbItem { Title = "Ep 1", Season = 1, Episode = 1, Type = "main" }
                                },
                                new DiscDbTitle
                                {
                                    Index = 2, Duration = 46 * 60,
                                    Item = new DiscDbItem { Title = "Ep 2", Season = 1, Episode = 2, Type = "main" }
                                },
                                new DiscDbTitle
                                {
                                    Index = 3, Duration = 44 * 60,
                                    Item = new DiscDbItem { Title = "Ep 3", Season = 1, Episode = 3, Type = "main" }
                                }
                            ]
                        }
                    ]
                }
            ]
        };

        var confidence = await service.MapTracksAsync(job, discDb, CancellationToken.None);

        Assert.True(confidence > 0.8, $"Expected avg confidence > 0.8, got {confidence:F2}");
        Assert.Equal(1, track1.EpisodeNumber);
        Assert.Equal(2, track2.EpisodeNumber);
        Assert.Equal(3, track3.EpisodeNumber);
        Assert.Equal("Ep 1", track1.EpisodeTitle);
        Assert.Equal("Ep 2", track2.EpisodeTitle);
        Assert.Equal("Ep 3", track3.EpisodeTitle);
    }
}
