using ArmMedia.Core.Abstractions;
using ArmMedia.Core.Services;
using Xunit;

namespace ArmMedia.Tests.TitleNormalization;

public sealed class TitleNormalizerTests
{
    private readonly ITitleNormalizer _normalizer = new TitleNormalizer();

    [Theory]
    [InlineData("Season 3 Disc 2", 3, 2, null, null, "")]
    [InlineData("S03", 3, null, null, null, "")]
    [InlineData("Season 1", 1, null, null, null, "")]
    [InlineData("S5E08", 5, null, null, "08", "")]
    [InlineData("Game of Thrones Season 4", 4, null, null, null, "game of thrones")]
    [InlineData("Blu-Ray Edition", null, null, "bluray", null, "edition")]
    [InlineData("The Matrix", null, null, null, null, "the matrix")]
    [InlineData("S02 Disc 1", 2, 1, null, null, "")]
    [InlineData("How I Met Your Mother S2D1", 2, 1, null, null, "how i met your mother")]
    public void Normalize_ExtractsHintsCorrectly(
        string input,
        int? expectedSeason,
        int? expectedDisc,
        string? expectedEdition,
        string? expectedEpisode,
        string expectedQuery)
    {
        var result = _normalizer.Normalize(input);

        Assert.Equal(expectedQuery, result.Query);
        Assert.Equal(expectedSeason, result.Season);
        Assert.Equal(expectedDisc, result.Disc);
        Assert.Equal(expectedEdition, result.Edition);
        Assert.Equal(expectedEpisode, result.EpisodeHint);
    }

    [Theory]
    [InlineData("Game of Thrones Season 4", "Season 4", null)]
    [InlineData("S03", "S03", null)]
    [InlineData("E05", null, "E05")]
    [InlineData("Season 1 Disc 1 Blu-Ray Edition", "Season 1", "Disc 1")]
    public void Normalize_RemovesExtractedTokensFromQuery(
        string input,
        string? seasonToken,
        string? discToken)
    {
        var result = _normalizer.Normalize(input);

        // The query should not contain the extracted tokens
        if (seasonToken is not null)
            Assert.DoesNotContain(seasonToken, result.Query, StringComparison.OrdinalIgnoreCase);
        if (discToken is not null)
            Assert.DoesNotContain(discToken, result.Query, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Normalize_EmptyString_ReturnsEmptyQuery()
    {
        var result = _normalizer.Normalize("");
        Assert.Equal("", result.Query);
        Assert.Null(result.Season);
        Assert.Null(result.Disc);
        Assert.Null(result.Edition);
    }

    [Fact]
    public void Normalize_NullString_ReturnsEmptyQuery()
    {
        var result = _normalizer.Normalize(null!);
        Assert.Equal("", result.Query);
    }

    [Fact]
    public void Normalize_WhitespaceOnly_ReturnsEmptyQuery()
    {
        var result = _normalizer.Normalize("   ");
        Assert.Equal("", result.Query);
    }

    [Fact]
    public void Normalize_PreservesApostrophes()
    {
        var result = _normalizer.Normalize("Everybody Loves Raymond Season 3");
        Assert.Contains("raymond", result.Query, StringComparison.OrdinalIgnoreCase);
        // Apostrophes should be preserved
        Assert.DoesNotContain("'", result.Query);
    }

    [Theory]
    [InlineData("Weeds_S2_Disc_1", "weeds", 2, 1)]
    [InlineData("WEEDS_SEASON_1_DISC_2", "weeds", 1, 2)]
    [InlineData("TRUEBLOOD_S5_DISC2", "trueblood", 5, 2)]
    public void Normalize_UnderscoreSeparatedDiscNames_ExtractsSeriesSeasonDisc(
        string input,
        string expectedSeries,
        int expectedSeason,
        int expectedDisc)
    {
        var result = _normalizer.Normalize(input);

        Assert.Equal(expectedSeries, result.Query);
        Assert.Equal(expectedSeason, result.Season);
        Assert.Equal(expectedDisc, result.Disc);
        Assert.Null(result.Edition);
        Assert.Null(result.EpisodeHint);
    }

    [Theory]
    [InlineData("Season 3 Disc 2 Episodes 13-18", "13-18")]
    [InlineData("S01E05", "05")]
    [InlineData("Episode 3", "3")]
    public void Normalize_ExtractsEpisodeHints(string input, string expectedEpisode)
    {
        var result = _normalizer.Normalize(input);
        Assert.Equal(expectedEpisode, result.EpisodeHint);
    }

    [Theory]
    [InlineData("4K UHD", "4k")]
    [InlineData("Blu-Ray", "bluray")]
    [InlineData("Director's Cut", "director'scut")]
    [InlineData("Extended Edition", "extended")]
    public void Normalize_ExtractsEditions(string input, string expectedEdition)
    {
        var result = _normalizer.Normalize(input);
        Assert.Equal(expectedEdition, result.Edition);
    }
}
