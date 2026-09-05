using ArmMedia.Core.Abstractions;
using ArmMedia.Core.Services;
using ArmRipper.Core.Models;
using ArmRipper.Core.Rip;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArmRipper.Core.Tests;

/// <summary>
/// Tests for <see cref="IdentifyService.ApplySeasonDiscFromTitleAndLabel"/> —
/// the TV series season/disc auto-population from title + disc label.
/// </summary>
public sealed class IdentifyServiceTests
{
    private static readonly ITitleNormalizer Normalizer = new TitleNormalizer();

    private static Job CreateSeriesJob(string? title, string? label) => new()
    {
        Id = 1,
        Title = title,
        Label = label,
        VideoType = VideoContentType.Series
    };

    [Fact]
    public void ApplySeasonDisc_TitleHasSeasonAndDisc_SetsBoth()
    {
        var job = CreateSeriesJob("Show Season 3 Disc 2", "SHOW_S3_D2");

        IdentifyService.ApplySeasonDiscFromTitleAndLabel(job, Normalizer, NullLogger.Instance);

        Assert.Equal(3, job.SeasonNumberAuto);
        Assert.Equal(3, job.SeasonNumber);
        Assert.Equal(2, job.DiscNumberAuto);
        Assert.Equal(2, job.DiscNumber);
    }

    [Fact]
    public void ApplySeasonDisc_TitleHasSeasonOnly_LabelDiscFillsGap()
    {
        // Regression for the reported bug: title "Show Season 5" normalizes to
        // season 5 with no disc. The label "SHOW_S5_D2" carries the disc and
        // must be picked up even though the title already provided a season.
        var job = CreateSeriesJob("Show Season 5", "SHOW_S5_D2");

        IdentifyService.ApplySeasonDiscFromTitleAndLabel(job, Normalizer, NullLogger.Instance);

        Assert.Equal(5, job.SeasonNumberAuto);
        Assert.Equal(5, job.SeasonNumber);
        Assert.Equal(2, job.DiscNumberAuto);
        Assert.Equal(2, job.DiscNumber);
    }

    [Fact]
    public void ApplySeasonDisc_TitleHasDiscOnly_LabelSeasonFillsGap()
    {
        var job = CreateSeriesJob("Show Disc 1", "SHOW_S2_D1");

        IdentifyService.ApplySeasonDiscFromTitleAndLabel(job, Normalizer, NullLogger.Instance);

        Assert.Equal(2, job.SeasonNumberAuto);
        Assert.Equal(2, job.SeasonNumber);
        Assert.Equal(1, job.DiscNumberAuto);
        Assert.Equal(1, job.DiscNumber);
    }

    [Fact]
    public void ApplySeasonDisc_TitleWinsOverLabel_ForValuesTitleProvides()
    {
        // Title provides season 3 and disc 2; label says season 9 disc 9.
        // Title values must win — the label only fills gaps.
        var job = CreateSeriesJob("Show Season 3 Disc 2", "SHOW_S9_D9");

        IdentifyService.ApplySeasonDiscFromTitleAndLabel(job, Normalizer, NullLogger.Instance);

        Assert.Equal(3, job.SeasonNumberAuto);
        Assert.Equal(3, job.SeasonNumber);
        Assert.Equal(2, job.DiscNumberAuto);
        Assert.Equal(2, job.DiscNumber);
    }

    [Fact]
    public void ApplySeasonDisc_LabelOnly_WhenTitleHasNoHints()
    {
        var job = CreateSeriesJob("Show", "SHOW_S4_D3");

        IdentifyService.ApplySeasonDiscFromTitleAndLabel(job, Normalizer, NullLogger.Instance);

        Assert.Equal(4, job.SeasonNumberAuto);
        Assert.Equal(4, job.SeasonNumber);
        Assert.Equal(3, job.DiscNumberAuto);
        Assert.Equal(3, job.DiscNumber);
    }

    [Fact]
    public void ApplySeasonDisc_EmptyTitle_NoOp()
    {
        var job = CreateSeriesJob("", "SHOW_S4_D3");

        IdentifyService.ApplySeasonDiscFromTitleAndLabel(job, Normalizer, NullLogger.Instance);

        Assert.Null(job.SeasonNumberAuto);
        Assert.Null(job.DiscNumberAuto);
    }

    [Fact]
    public void ApplySeasonDisc_ManualOverrides_AreNotOverwritten()
    {
        // Manual values (SeasonNumber/DiscNumber) must survive; only the
        // Auto fields are populated.
        var job = CreateSeriesJob("Show Season 3 Disc 2", "SHOW_S3_D2");
        job.SeasonNumber = 7;
        job.DiscNumber = 8;

        IdentifyService.ApplySeasonDiscFromTitleAndLabel(job, Normalizer, NullLogger.Instance);

        Assert.Equal(3, job.SeasonNumberAuto);
        Assert.Equal(7, job.SeasonNumber); // manual wins
        Assert.Equal(2, job.DiscNumberAuto);
        Assert.Equal(8, job.DiscNumber);   // manual wins
    }
}