using System.Reflection;
using ArmRipper.Core.Models;

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
    [InlineData("movie", "movies")]
    [InlineData("series", "tv")]
    [InlineData("MOVIE", "movies")]
    [InlineData("SERIES", "tv")]
    [InlineData("unknown", "unidentified")]
    [InlineData(null, "unidentified")]
    [InlineData("", "unidentified")]
    public void ConvertJobType_VariousInputs_ReturnsCorrectFolder(string? input, string expected)
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
}
