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
    public void ApplySeasonDisc_CompactUnderscoreLabel_WestworldPattern()
    {
        // Regression for the compact "_S1_D2" label form (e.g. "WESTWORLD_S1_D2")
        // where both season and disc use single-letter compact notation.
        var job = CreateSeriesJob("Westworld", "WESTWORLD_S1_D2");

        IdentifyService.ApplySeasonDiscFromTitleAndLabel(job, Normalizer, NullLogger.Instance);

        Assert.Equal(1, job.SeasonNumberAuto);
        Assert.Equal(1, job.SeasonNumber);
        Assert.Equal(2, job.DiscNumberAuto);
        Assert.Equal(2, job.DiscNumber);
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

    // ─────────────────────────────────────────────────────────────────────────
    // ParseLsdvdAspectRatios
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ParseLsdvdAspectRatios_TypicalDvd_ExtractsBothTracks()
    {
        // Simulates lsdvd -Oyv output for a DVD with two aspect ratios
        var output = """
            lsdvd = {
              'device' : '/dev/sr1',
              'title' : 'TEST_DISC',
              'track' : [
                {
                  'ix' : 1,
                  'length' : 5873.700,
                  'vts_id' : 'DVDVIDEO-VTS',
                  'aspect' : 1.777778,
                  'format' : 'NTSC',
                },
                {
                  'ix' : 2,
                  'length' : 125.700,
                  'vts_id' : 'DVDVIDEO-VTS',
                  'aspect' : 1.333333,
                  'format' : 'NTSC',
                },
              ],
              'longest_track' : 1,
            }
            """;

        var result = IdentifyService.ParseLsdvdAspectRatios(output);

        Assert.Equal(2, result.Count);
        Assert.Equal("16:9", result[0]);
        Assert.Equal("4:3", result[1]);
    }

    [Fact]
    public void ParseLsdvdAspectRatios_DropDeadGorgeous_ExtractsCorrectDars()
    {
        // The actual scenario: both tracks report ~1.333333 (4:3 SAR) from MakeMKV,
        // but lsdvd shows the true DAR for each track.
        var output = """
            lsdvd = {
              'device' : '/dev/sr1',
              'title' : 'DROP_DEAD_GORGEOUS',
              'track' : [
                {
                  'ix' : 1,
                  'length' : 5873.700,
                  'aspect' : 1.777778,
                },
                {
                  'ix' : 2,
                  'length' : 125.700,
                  'aspect' : 1.333333,
                },
                {
                  'ix' : 3,
                  'length' : 109.433,
                  'aspect' : 1.333333,
                },
                {
                  'ix' : 4,
                  'length' : 159.400,
                  'aspect' : 1.333333,
                },
                {
                  'ix' : 5,
                  'length' : 156.734,
                  'aspect' : 1.333333,
                },
                {
                  'ix' : 6,
                  'length' : 5873.700,
                  'aspect' : 1.333333,
                },
              ],
              'longest_track' : 1,
            }
            """;

        var result = IdentifyService.ParseLsdvdAspectRatios(output);

        Assert.Equal(6, result.Count);
        Assert.Equal("16:9", result[0]);  // track 1 (ix=1) → index 0
        Assert.Equal("4:3", result[1]);  // track 2 (ix=2) → index 1
        Assert.Equal("4:3", result[5]);  // track 6 (ix=6) → index 5
    }

    [Fact]
    public void ParseLsdvdAspectRatios_NoTrackSection_ReturnsEmpty()
    {
        var output = "lsdvd = { 'device' : '/dev/sr1' }";
        var result = IdentifyService.ParseLsdvdAspectRatios(output);
        Assert.Empty(result);
    }

    [Fact]
    public void ParseLsdvdAspectRatios_EmptyInput_ReturnsEmpty()
    {
        Assert.Empty(IdentifyService.ParseLsdvdAspectRatios(""));
        Assert.Empty(IdentifyService.ParseLsdvdAspectRatios("   "));
    }

    [Fact]
    public void ParseLsdvdAspectRatios_NoAspectField_DefaultsNotAdded()
    {
        // Without -v, lsdvd output has no 'aspect' field
        var output = """
            lsdvd = {
              'track' : [
                { 'ix' : 1, 'length' : 5873.700 },
                { 'ix' : 2, 'length' : 125.700 },
              ],
            }
            """;

        var result = IdentifyService.ParseLsdvdAspectRatios(output);
        Assert.Empty(result);
    }
}