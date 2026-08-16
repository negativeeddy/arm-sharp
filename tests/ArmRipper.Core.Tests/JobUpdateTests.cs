using ArmRipper.Core.Models;

namespace ArmRipper.Core.Tests;

public sealed class JobUpdateTests
{
    [Fact]
    public void FromJob_MapsIdentificationFields()
    {
        var job = TestHelpers.CreateTestJob(j =>
        {
            j.Title = "The Matrix";
            j.TitleAuto = "The Matrix";
            j.TitleManual = "My Matrix";
            j.Year = "1999";
            j.YearAuto = "1999";
            j.YearManual = "2000";
            j.VideoType = VideoContentType.Movie;
            j.VideoTypeAuto = VideoContentType.Movie;
            j.VideoTypeManual = VideoContentType.Episode;
            j.ImdbId = "tt0133093";
            j.ImdbIdAuto = "tt0133093";
            j.ImdbIdManual = "tt0000000";
            j.PosterUrl = "https://example.com/a.jpg";
            j.PosterUrlAuto = "https://example.com/a.jpg";
            j.PosterUrlManual = "https://example.com/b.jpg";
            j.SeasonNumber = 3;
            j.SeasonNumberAuto = 3;
            j.SeasonNumberManual = 4;
            j.DiscNumber = 1;
            j.DiscNumberAuto = 1;
            j.DiscNumberManual = 2;
            j.StartingEpisodeNumber = 7;
            j.HasNiceTitle = true;
        });

        var update = JobUpdate.FromJob(job);

        Assert.Equal(job.Title, update.Title);
        Assert.Equal(job.TitleAuto, update.TitleAuto);
        Assert.Equal(job.Year, update.Year);
        Assert.Equal(job.YearAuto, update.YearAuto);
        Assert.Equal(job.VideoType, update.VideoType);
        Assert.Equal(job.VideoTypeAuto, update.VideoTypeAuto);
        Assert.Equal(job.ImdbIdAuto, update.ImdbIdAuto);
        Assert.Equal(job.PosterUrl, update.PosterUrl);
        Assert.Equal(job.PosterUrlAuto, update.PosterUrlAuto);
        Assert.Equal(job.SeasonNumber, update.SeasonNumber);
        Assert.Equal(job.SeasonNumberAuto, update.SeasonNumberAuto);
        Assert.Equal(job.DiscNumber, update.DiscNumber);
        Assert.Equal(job.DiscNumberAuto, update.DiscNumberAuto);
        Assert.Equal(job.StartingEpisodeNumber, update.StartingEpisodeNumber);
        Assert.True(update.HasNiceTitle);
    }

    [Fact]
    public void FromJob_UnidentifiedJob_LeavesIdentificationFieldsNull()
    {
        var job = TestHelpers.CreateTestJob(j =>
        {
            j.Title = null;
            j.TitleAuto = null;
            j.Year = null;
            j.YearAuto = null;
            j.VideoType = VideoContentType.Unknown;
            j.VideoTypeAuto = null;
            j.ImdbIdAuto = null;
            j.PosterUrl = null;
            j.PosterUrlAuto = null;
            j.SeasonNumber = null;
            j.SeasonNumberAuto = null;
            j.DiscNumber = null;
            j.DiscNumberAuto = null;
            j.StartingEpisodeNumber = null;
            j.HasNiceTitle = false;
        });

        var update = JobUpdate.FromJob(job);

        Assert.Null(update.Title);
        Assert.Null(update.TitleAuto);
        Assert.Null(update.Year);
        Assert.Null(update.YearAuto);
        Assert.Equal(VideoContentType.Unknown, update.VideoType);
        Assert.Null(update.VideoTypeAuto);
        Assert.Null(update.ImdbIdAuto);
        Assert.Null(update.PosterUrl);
        Assert.Null(update.PosterUrlAuto);
        Assert.Null(update.SeasonNumber);
        Assert.Null(update.SeasonNumberAuto);
        Assert.Null(update.DiscNumber);
        Assert.Null(update.DiscNumberAuto);
        Assert.Null(update.StartingEpisodeNumber);
        Assert.False(update.HasNiceTitle);
    }
}
