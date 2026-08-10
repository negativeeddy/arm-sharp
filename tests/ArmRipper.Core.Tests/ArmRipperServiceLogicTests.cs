using System.Reflection;
using ArmRipper.Core.Models;
using ArmRipper.Core.Rip;

namespace ArmRipper.Core.Tests;

public sealed class ArmRipperServiceLogicTests
{
    private static MethodInfo GetStaticMethod(string name)
    {
        var type = typeof(ArmRipper.Core.Rip.ArmRipperService);
        var method = type.GetMethod(name,
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);
        return method;
    }

    [Theory]
    [InlineData(VideoContentType.Movie, "movies")]
    [InlineData(VideoContentType.Series, "tv")]
    [InlineData(VideoContentType.Tv, "unidentified")]
    [InlineData(VideoContentType.Episode, "unidentified")]
    [InlineData(VideoContentType.Unknown, "unidentified")]
    public void ConvertJobType_VariousInputs_ReturnsCorrectFolder(VideoContentType input, string expected)
    {
        var method = GetStaticMethod("ConvertJobType");
        var result = method.Invoke(null, [input]);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void FixJobTitle_WithYearAndNoManual_ReturnsTitleWithYear()
    {
        var job = TestHelpers.CreateTestJob(j => { j.Year = "1999"; j.TitleManual = null; });
        var method = GetStaticMethod("FixJobTitle");
        var result = method.Invoke(null, [job]);
        Assert.Equal("Test Movie (1999)", result);
    }

    [Fact]
    public void FixJobTitle_WithYearAndManual_ReturnsManualWithYear()
    {
        var job = TestHelpers.CreateTestJob(j =>
        {
            j.Year = "1999";
            j.TitleManual = "My Manual Title";
        });
        var method = GetStaticMethod("FixJobTitle");
        var result = method.Invoke(null, [job]);
        Assert.Equal("My Manual Title (1999)", result);
    }

    [Fact]
    public void FixJobTitle_WithoutYear_ReturnsTitleOnly()
    {
        var job = TestHelpers.CreateTestJob(j =>
        {
            j.Year = null;
            j.TitleManual = null;
        });
        var method = GetStaticMethod("FixJobTitle");
        var result = method.Invoke(null, [job]);
        Assert.Equal("Test Movie", result);
    }

    [Fact]
    public void FixJobTitle_YearIs0000_ReturnsTitleOnly()
    {
        var job = TestHelpers.CreateTestJob(j =>
        {
            j.Year = "0000";
            j.TitleManual = null;
        });
        var method = GetStaticMethod("FixJobTitle");
        var result = method.Invoke(null, [job]);
        Assert.Equal("Test Movie", result);
    }

    [Fact]
    public void FixJobTitle_WithManualOnly_ReturnsManualTitle()
    {
        var job = TestHelpers.CreateTestJob(j =>
        {
            j.Year = null;
            j.TitleManual = "Custom Title";
        });
        var method = GetStaticMethod("FixJobTitle");
        var result = method.Invoke(null, [job]);
        Assert.Equal("Custom Title", result);
    }

    [Theory]
    [InlineData(DiscType.Bluray, false, "mkv", false, true)]
    [InlineData(DiscType.Dvd, false, "mkv", true, true)]
    [InlineData(DiscType.Dvd, true, "mkv", true, true)]
    [InlineData(DiscType.Dvd, true, "mkv", false, true)]
    [InlineData(DiscType.Dvd, false, "backup_dvd", false, true)]
    [InlineData(DiscType.Dvd, false, "mkv", false, true)]
    public void RipWithMkv_ReturnsExpected(DiscType discType, bool skipTranscode, string ripMethod, bool protection, bool expected)
    {
        var job = TestHelpers.CreateTestJob(
            j =>
            {
                j.DiscType = discType;
                j.HasTrack99 = protection;
            },
            c =>
            {
                c.SkipTranscode = skipTranscode;
                c.RipMethod = ripMethod;
                c.MainFeature = false;
            });

        var type = typeof(ArmRipper.Core.Rip.ArmRipperService);
        var method = type.GetMethod("RipWithMkv",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method.Invoke(null, [job, protection]);
        Assert.Equal(expected, result);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // CleanSeriesTitle
    // ─────────────────────────────────────────────────────────────────────────

    private static string InvokeCleanSeriesTitle(string raw)
    {
        var method = GetStaticMethod("CleanSeriesTitle");
        return (string)method.Invoke(null, [raw])!;
    }

    [Theory]
    [InlineData("How I Met Your Mother S3D1",          "How I Met Your Mother")]
    [InlineData("How I Met Your Mother S3D2",          "How I Met Your Mother")]
    [InlineData("MY_NAME_IS_EARL_S1_D1",               "My Name Is Earl")]
    [InlineData("MY_NAME_IS_EARL_SEASON1_DISC2",       "My Name Is Earl")]
    [InlineData("Seinfeld S08D03",                      "Seinfeld")]
    [InlineData("Seinfeld Season8Disc3",                "Seinfeld")]
    [InlineData("Game of Thrones Season2 Disc1",        "Game of Thrones")]
    [InlineData("How I Met Your Mother S3D1 (2005)",    "How I Met Your Mother")]
    [InlineData("Friends",                              "Friends")]
    [InlineData("THE_OFFICE",                           "The Office")]
    [InlineData("Simpsons_S10_D2",                      "Simpsons")]
    [InlineData("HOW_I_MET_YOUR_MOTHER_S2_D1_US",       "How I Met Your Mother")]
    [InlineData("HOW_I_MET_YOUR_MOTHER_S3_D1",          "How I Met Your Mother")]
    public void CleanSeriesTitle_VariousFormats_ReturnsCleanTitle(string input, string expected)
    {
        var result = InvokeCleanSeriesTitle(input);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CleanSeriesTitle_NullOrEmpty_ReturnsUnknownSeries(string? input)
    {
        var result = InvokeCleanSeriesTitle(input ?? "");
        Assert.Equal("Unknown Series", result);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // IsRipUndersized / MinRipSizeRatio
    // ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(100_000, 10_000, 0.30, true)]   // ~10% of expected — grossly undersized
    [InlineData(100_000, 29_999, 0.30, true)]   // just under the 30% threshold
    [InlineData(100_000, 30_000, 0.30, false)]  // exactly at the threshold
    [InlineData(100_000, 99_000, 0.30, false)]  // healthy rip
    [InlineData(0, 10_000, 0.30, false)]        // no expected size — cannot validate
    public void IsRipUndersized_VariousSizes_ReturnsExpected(long expected, long actual, double minRatio, bool expectedResult)
    {
        var method = GetStaticMethod("IsRipUndersized");
        var result = method.Invoke(null, [expected, actual, minRatio]);
        Assert.Equal(expectedResult, result);
    }

    [Fact]
    public void MinRipSizeRatio_IsThirtyPercent()
    {
        var field = typeof(ArmRipper.Core.Rip.ArmRipperService)
            .GetField("MinRipSizeRatio",
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(field);
        Assert.Equal(0.30, (double)field.GetValue(null)!);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // IsRipDurationTruncated / IsRipDurationBelowMinLength / MinDurationRatio
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void MinDurationRatio_IsFiftyPercent()
    {
        var field = typeof(ArmRipper.Core.Rip.ArmRipperService)
            .GetField("MinDurationRatio",
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(field);
        Assert.Equal(0.50, (double)field.GetValue(null)!);
    }

    [Theory]
    [InlineData(6547.0, 9.0, true)]        // 1:49:15 expected → 9s salvaged clip (job 977)
    [InlineData(6547.0, 6540.0, false)]    // healthy rip
    [InlineData(6547.0, 3273.5, false)]    // exactly 50% — not below the threshold
    [InlineData(6547.0, 3273.0, true)]     // just under 50%
    [InlineData(0.0, 9.0, false)]          // no expected duration — cannot validate
    [InlineData(6547.0, null, false)]      // ffprobe failed — cannot validate
    [InlineData(null, 9.0, false)]         // no expected duration — cannot validate
    public void IsRipDurationTruncated_VariousDurations_ReturnsExpected(
        double? expected, double? actual, bool expectedResult)
    {
        var result = ArmRipperService.IsRipDurationTruncated(expected, actual, ArmRipperService.MinDurationRatio);
        Assert.Equal(expectedResult, result);
    }

    [Theory]
    [InlineData(9.0, 300, true)]      // 9s clip below 5-minute floor
    [InlineData(310.0, 300, false)]   // above floor
    [InlineData(300.0, 300, false)]   // exactly at floor — not below
    [InlineData(9.0, 0, false)]       // no floor configured
    [InlineData(null, 300, false)]    // ffprobe failed — cannot validate
    public void IsRipDurationBelowMinLength_VariousDurations_ReturnsExpected(
        double? actual, int minLength, bool expectedResult)
    {
        var result = ArmRipperService.IsRipDurationBelowMinLength(actual, minLength);
        Assert.Equal(expectedResult, result);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // VerifyRipOutput
    // ─────────────────────────────────────────────────────────────────────────

    private static ArmRipperService.RipVerificationVerdict InvokeVerifyRipOutput(
        bool isMainFeature, long expectedSize, long actualSize,
        double? expectedDuration, double? actualDuration, int minLength)
        => ArmRipperService.VerifyRipOutput(
            isMainFeature, expectedSize, actualSize,
            expectedDuration, actualDuration, minLength,
            ArmRipperService.MinRipSizeRatio, ArmRipperService.MinDurationRatio);

    [Fact]
    public void VerifyRipOutput_HealthyRip_ReturnsPass()
    {
        var result = InvokeVerifyRipOutput(
            isMainFeature: true,
            expectedSize: 4_000_000_000, actualSize: 3_900_000_000,
            expectedDuration: 6547, actualDuration: 6540,
            minLength: 300);
        Assert.Equal(ArmRipperService.RipVerificationVerdict.Pass, result);
    }

    [Fact]
    public void VerifyRipOutput_MainFeatureUndersized_ReturnsFail()
    {
        var result = InvokeVerifyRipOutput(
            isMainFeature: true,
            expectedSize: 4_000_000_000, actualSize: 10_000,
            expectedDuration: 6547, actualDuration: 6540,
            minLength: 300);
        Assert.Equal(ArmRipperService.RipVerificationVerdict.Fail, result);
    }

    [Fact]
    public void VerifyRipOutput_ExtraUndersized_ReturnsWarn()
    {
        var result = InvokeVerifyRipOutput(
            isMainFeature: false,
            expectedSize: 4_000_000_000, actualSize: 10_000,
            expectedDuration: 6547, actualDuration: 6540,
            minLength: 300);
        Assert.Equal(ArmRipperService.RipVerificationVerdict.Warn, result);
    }

    [Fact]
    public void VerifyRipOutput_MainFeatureDurationTruncated_ReturnsFail()
    {
        // Size happens to coincide, but the probed duration is ~0.1% of expected —
        // the exact case B2's size gate alone cannot catch.
        var result = InvokeVerifyRipOutput(
            isMainFeature: true,
            expectedSize: 4_000_000_000, actualSize: 4_000_000_000,
            expectedDuration: 6547, actualDuration: 9,
            minLength: 300);
        Assert.Equal(ArmRipperService.RipVerificationVerdict.Fail, result);
    }

    [Fact]
    public void VerifyRipOutput_ExtraDurationTruncated_ReturnsWarn()
    {
        var result = InvokeVerifyRipOutput(
            isMainFeature: false,
            expectedSize: 4_000_000_000, actualSize: 4_000_000_000,
            expectedDuration: 6547, actualDuration: 9,
            minLength: 300);
        Assert.Equal(ArmRipperService.RipVerificationVerdict.Warn, result);
    }

    [Fact]
    public void VerifyRipOutput_MainFeatureBelowMinLengthFloor_ReturnsFail()
    {
        var result = InvokeVerifyRipOutput(
            isMainFeature: true,
            expectedSize: 4_000_000_000, actualSize: 4_000_000_000,
            expectedDuration: 6547, actualDuration: 250,
            minLength: 300);
        Assert.Equal(ArmRipperService.RipVerificationVerdict.Fail, result);
    }

    [Fact]
    public void VerifyRipOutput_CannotProbeDuration_SizeHealthy_ReturnsPass()
    {
        // ffprobe returned null — the size gate still passes so we must not fail.
        var result = InvokeVerifyRipOutput(
            isMainFeature: true,
            expectedSize: 4_000_000_000, actualSize: 3_900_000_000,
            expectedDuration: 6547, actualDuration: null,
            minLength: 300);
        Assert.Equal(ArmRipperService.RipVerificationVerdict.Pass, result);
    }

    [Fact]
    public void VerifyRipOutput_NoExpectedData_ReturnsPass()
    {
        var result = InvokeVerifyRipOutput(
            isMainFeature: true,
            expectedSize: 0, actualSize: 10_000,
            expectedDuration: null, actualDuration: 9,
            minLength: 0);
        Assert.Equal(ArmRipperService.RipVerificationVerdict.Pass, result);
    }

    [Fact]
    public void ComputeOutputPath_MovieTitleAndYear_ReturnsMoviesFolder()
    {
        var job = TestHelpers.CreateTestJob();
        Assert.Equal(
            "/home/arm/media/completed/movies/Test Movie (2024)",
            ArmRipperService.ComputeOutputPath(job, null));
    }

    [Fact]
    public void ComputeOutputPath_SeriesManualTitle_ReturnsTvFolder()
    {
        var job = TestHelpers.CreateTestJob(j =>
        {
            j.VideoType = VideoContentType.Series;
            j.TitleManual = "My Series";
            j.Year = "2001";
        });
        Assert.Equal(
            "/home/arm/media/completed/tv/My Series (2001)",
            ArmRipperService.ComputeOutputPath(job, null));
    }

    [Fact]
    public void ComputeOutputPath_UnknownType_ReturnsUnidentifiedFolder()
    {
        var job = TestHelpers.CreateTestJob(j => j.VideoType = VideoContentType.Unknown);
        Assert.Equal(
            "/home/arm/media/completed/unidentified/Test Movie (2024)",
            ArmRipperService.ComputeOutputPath(job, null));
    }

    [Fact]
    public void ComputeOutputPath_CustomCompletedPath_IsUsed()
    {
        var job = TestHelpers.CreateTestJob();
        Assert.Equal(
            "/custom/media/completed/movies/Test Movie (2024)",
            ArmRipperService.ComputeOutputPath(job, "/custom/media/completed"));
    }
}
