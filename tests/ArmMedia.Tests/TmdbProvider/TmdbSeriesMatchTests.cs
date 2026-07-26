using Xunit;

namespace ArmMedia.Tests.TmdbProvider;

public class TmdbSeriesMatchTests
{
    private static double Score(string expected, string? candidate)
        => global::ArmMedia.TmdbProvider.TmdbProvider.ScoreSeriesMatch(expected, candidate);

    [Theory]
    [InlineData("How I Met Your Mother", "How I Met Your Mother", 1.0)]
    [InlineData("How I Met Your Mother", "how i met your mother", 1.0)]
    [InlineData("The Big Bang Theory", "The Big Bang Theory", 1.0)]
    public void ExactMatch_Returns1(string expected, string candidate, double expectedScore)
    {
        Assert.Equal(expectedScore, Score(expected, candidate), 2);
    }

    [Theory]
    [InlineData("How I Met Your Mother", "How I Met Your Mother (TV Series)")]
    [InlineData("Breaking Bad", "Breaking Bad (2008)")]
    [InlineData("The Office", "The Office (US)")]
    public void CandidateLongerThanQuery_ScoresHigh(string expected, string candidate)
    {
        Assert.InRange(Score(expected, candidate), 0.7, 1.0);
    }

    [Theory]
    [InlineData("How I Met Your Mother", "Friends")]
    [InlineData("Breaking Bad", "The Voice")]
    [InlineData("The Office", "Suits")]
    public void CompletelyDifferentTitles_ScoreLow(string expected, string candidate)
    {
        Assert.InRange(Score(expected, candidate), 0, 0.3);
    }

    [Theory]
    [InlineData("The Office US", "The Office")]
    [InlineData("Game of Thrones", "Game of Thrones Extended")]
    public void QueryIsLongerThanCandidate_PartialMatch(string expected, string candidate)
    {
        Assert.InRange(Score(expected, candidate), 0.5, 1.0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NullOrEmptyCandidate_Returns0(string? candidate)
    {
        Assert.Equal(0, Score("Some Title", candidate));
    }

    [Fact]
    public void SharedTokenBoost_AllQueryTokensInCandidate()
    {
        Assert.InRange(Score("How I Met", "How I Met Your Mother"), 0.7, 1.0);
    }

    [Theory]
    [InlineData("Seinfeld", "Seinfeld (1989)")]
    [InlineData("Lost", "LOST")]
    public void CaseInsensitiveMatch(string expected, string candidate)
    {
        Assert.InRange(Score(expected, candidate), 0.7, 1.0);
    }

    [Fact]
    public void PartialOverlap_ScoresModerate()
    {
        Assert.InRange(Score("The Big Bang Theory", "The Big Lebowski"), 0.1, 0.6);
    }

    [Fact]
    public void PunctuationStripped_MatchesCleanTitle()
    {
        Assert.InRange(Score("Mr. Robot", "Mr Robot"), 0.8, 1.0);
    }

    [Fact]
    public void CandidateFirstSeason_ScoresHigh()
    {
        Assert.InRange(
            Score("How I Met Your Mother", "How I Met Your Mother: Season 2"),
            0.7, 1.0);
    }
}
