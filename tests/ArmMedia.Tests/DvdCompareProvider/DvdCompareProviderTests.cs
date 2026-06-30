using ArmMedia.Core.Models;
using Xunit;
using DvdCompare = ArmMedia.DvdCompareProvider;

namespace ArmMedia.Tests.DvdCompareProvider;

/// <summary>
/// Tests for <see cref="DvdCompare.DvdCompareProvider"/>
/// HTML parsing and episode matching logic.
/// </summary>
public sealed class DvdCompareProviderTests
{
    /// <summary>
    /// Simulated Extras section content from the R1 America release of
    /// "My Name Is Earl: Season 1" (dvdcompare.net fid=9923).
    /// Contains DISC ONE, DISC TWO, DISC THREE with episode titles and runtimes.
    /// </summary>
    private const string SampleExtrasContent =
        "<b>DISC ONE</b> \n" +
        "<br />7 Episodes (with Play All)\n" +
        "<br />- \"Pilot\" (24:51)\n" +
        "<br />- \"Quit Smoking\" (21:36)\n" +
        "<br />- \"Randy's Touchdown\" (21:35)\n" +
        "<br />- \"Faken My Own Death\" (21:29)\n" +
        "<br />- \"Teacher Earl\" (21:36)\n" +
        "<br />- \"Broke Joy's Fancy Figurine\" (21:35)\n" +
        "<br />- \"Stole Beer from a Golfer\" (21:40)\n" +
        "<br />Audio Commentary on \"Pilot\"...\n" +
        "<br />\n" +
        "<br /><b>DISC TWO</b> \n" +
        "<br />7 Episodes (with Play All)\n" +
        "<br />- \"Joy's Wedding\" (21:39)\n" +
        "<br />- \"Cost Dad an Election\" (21:35)\n" +
        "<br />- \"White Lie Christmas\" (21:39)\n" +
        "<br />- \"Barn Burner\" (21:56)\n" +
        "<br />- \"O Karma, Where Art Though?\" (21:38)\n" +
        "<br />- \"Stole P's HD Cart\" (21:40)\n" +
        "<br />- \"Monkeys in Space\" (21:41)\n" +
        "<br />\n" +
        "<br /><b>DISC THREE</b> \n" +
        "<br />7 Episodes (with Play All)\n" +
        "<br />- \"Something to Live For\" (21:13)\n" +
        "<br />- \"The Professor\" (21:40)\n" +
        "<br />- \"Didn't Pay Taxes\" (21:20)\n" +
        "<br />- \"Dad's Car\" (21:41)\n" +
        "<br />- \"Y2K\" (21:36)\n" +
        "<br />- \"Boogeyman\" (21:05)\n" +
        "<br />- \"Bounty Hunter\" (21:41)\n";

    // ─────────────────────────────────────────────────────────────────────────
    // TC-DC-01: Parse disc groups from Extras content
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ParseDiscsFromExtrasContent_ReturnsThreeDiscs()
    {
        var discs = DvdCompare.DvdCompareProvider.ParseDiscsFromExtrasContent(SampleExtrasContent);

        Assert.NotNull(discs);
        Assert.Equal(3, discs.Count);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // TC-DC-02: Each disc has correct number of episodes
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ParseDiscsFromExtrasContent_DiscOneHasSevenEpisodes()
    {
        var discs = DvdCompare.DvdCompareProvider.ParseDiscsFromExtrasContent(SampleExtrasContent);

        Assert.Equal(7, discs[0].Episodes.Count);
        Assert.Equal(7, discs[1].Episodes.Count);
        Assert.Equal(7, discs[2].Episodes.Count);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // TC-DC-03: Episode titles parse correctly
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ParseDiscsFromExtrasContent_DiscOneTitles()
    {
        var discs = DvdCompare.DvdCompareProvider.ParseDiscsFromExtrasContent(SampleExtrasContent);
        var titles = discs[0].Episodes.Select(e => e.Title).ToArray();

        Assert.Equal("Pilot", titles[0]);
        Assert.Equal("Quit Smoking", titles[1]);
        Assert.Equal("Randy's Touchdown", titles[2]);
        Assert.Equal("Faken My Own Death", titles[3]);
        Assert.Equal("Teacher Earl", titles[4]);
        Assert.Equal("Broke Joy's Fancy Figurine", titles[5]);
        Assert.Equal("Stole Beer from a Golfer", titles[6]);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // TC-DC-04: Episode runtimes parse correctly (MM:SS → seconds)
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ParseDiscsFromExtrasContent_DiscOneRuntimes()
    {
        var discs = DvdCompare.DvdCompareProvider.ParseDiscsFromExtrasContent(SampleExtrasContent);
        var durations = discs[0].Episodes.Select(e => e.DurationSeconds).ToArray();

        // Pilot: 24:51 = 24*60 + 51 = 1491s
        Assert.Equal(1491, durations[0]);
        // Quit Smoking: 21:36 = 1296s
        Assert.Equal(1296, durations[1]);
        // Randy's Touchdown: 21:35 = 1295s
        Assert.Equal(1295, durations[2]);
        // Faken My Own Death: 21:29 = 1289s
        Assert.Equal(1289, durations[3]);
        // Teacher Earl: 21:36 = 1296s
        Assert.Equal(1296, durations[4]);
        // Broke Joy's Fancy Figurine: 21:35 = 1295s
        Assert.Equal(1295, durations[5]);
        // Stole Beer from a Golfer: 21:40 = 1300s
        Assert.Equal(1300, durations[6]);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // TC-DC-05: Disc numbers are 1-based in order
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ParseDiscsFromExtrasContent_DiscNumbersAreSequential()
    {
        var discs = DvdCompare.DvdCompareProvider.ParseDiscsFromExtrasContent(SampleExtrasContent);

        Assert.Equal(1, discs[0].DiscNumber);
        Assert.Equal(2, discs[1].DiscNumber);
        Assert.Equal(3, discs[2].DiscNumber);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // TC-DC-06: DiscTwo title parsing
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ParseDiscsFromExtrasContent_DiscTwoTitles()
    {
        var discs = DvdCompare.DvdCompareProvider.ParseDiscsFromExtrasContent(SampleExtrasContent);
        var titles = discs[1].Episodes.Select(e => e.Title).ToArray();

        Assert.Equal("Joy's Wedding", titles[0]);
        Assert.Equal("Monkeys in Space", titles[6]);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // TC-DC-07: Parsing a single-Extras-section HTML block
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ParseDiscEpisodeGroups_FromFullHtmlBlock()
    {
        // Simulate a full page HTML fragment with one Extras section
        string html = $"<html><body><div class=\"label\">Extras:</div><div class=\"description\">{SampleExtrasContent}</div></body></html>";

        var discs = DvdCompare.DvdCompareProvider.ParseDiscEpisodeGroups(html, releaseIndex: 0);

        Assert.NotNull(discs);
        Assert.Equal(3, discs.Count);
        Assert.Equal(7, discs[0].Episodes.Count);
        Assert.Equal("Pilot", discs[0].Episodes[0].Title);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // TC-DC-08: Retrieve correct release by index
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ParseDiscEpisodeGroups_SelectsCorrectRelease()
    {
        // HTML with two Extras sections (simulating R1 and R2)
        string html =
            "<div class=\"label\">Extras:</div><div class=\"description\">" +
            "<b>DISC ONE</b><br />- \"R1 Ep\" (10:00)" +
            "</div>" +
            "<div class=\"label\">Extras:</div><div class=\"description\">" +
            "<b>DISC ONE</b><br />- \"R2 Ep\" (20:00)" +
            "</div>";

        // Index 0 = first release
        var discs0 = DvdCompare.DvdCompareProvider.ParseDiscEpisodeGroups(html, releaseIndex: 0);
        Assert.Equal("R1 Ep", discs0![0].Episodes[0].Title);

        // Index 1 = second release
        var discs1 = DvdCompare.DvdCompareProvider.ParseDiscEpisodeGroups(html, releaseIndex: 1);
        Assert.Equal("R2 Ep", discs1![0].Episodes[0].Title);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // TC-DC-09: Empty HTML returns null
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ParseDiscEpisodeGroups_EmptyHtml_ReturnsNull()
    {
        var discs = DvdCompare.DvdCompareProvider.ParseDiscEpisodeGroups("<html></html>");
        Assert.Null(discs);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // TC-DC-10: Extras lines (non-episode) are filtered out
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ParseDiscsFromExtrasContent_FiltersOutNonEpisodeLines()
    {
        string content =
            "<b>DISC ONE</b> \n" +
            "<br />- \"Real Episode\" (22:00)\n" +
            "<br />Audio Commentary on \"Real Episode\" by Someone (30:00)\n" +
            "<br />- \"Another Episode\" (21:30)\n" +
            "<br />Deleted Scenes (with Play All) (5:00)\n" +
            "<br />- \"A Third Episode\" (20:00)\n";

        var discs = DvdCompare.DvdCompareProvider.ParseDiscsFromExtrasContent(content);
        Assert.NotNull(discs);
        Assert.Single(discs);
        Assert.Equal(3, discs[0].Episodes.Count);
        Assert.Equal("Real Episode", discs[0].Episodes[0].Title);
        Assert.Equal("Another Episode", discs[0].Episodes[1].Title);
        Assert.Equal("A Third Episode", discs[0].Episodes[2].Title);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // TC-DC-11: Search result parsing finds matching season
    // ─────────────────────────────────────────────────────────────────────────

    private const string SampleSearchHtml =
        "<html><body>" +
        "<a href=\"film.php?fid=9923\" onmouseover=\"changeStatus(this, 'Karma is a funny thing.'); return true\" title=\"Karma is a funny thing.\">My Name is Earl: Season 1 (TV) (2005-2006)</a>" +
        "<a href=\"film.php?fid=21363\" onmouseover=\"changeStatus(this, 'Karma is a funny thing.'); return true\" title=\"Karma is a funny thing.\">My Name is Earl: Season 2 (TV) (2006-2007)</a>" +
        "<a href=\"film.php?fid=21422\" onmouseover=\"changeStatus(this, 'Karma is a funny thing.'); return true\" title=\"Karma is a funny thing.\">My Name is Earl: Season 3 (TV) (2007-2008)</a>" +
        "</body></html>";

    [Fact]
    public void ParseSearchResults_FindsCorrectSeason()
    {
        var url = DvdCompare.DvdCompareProvider.ParseSearchResults(SampleSearchHtml, "My Name is Earl", 1);
        Assert.Equal("https://dvdcompare.net/comparisons/film.php?fid=9923", url);
    }

    [Fact]
    public void ParseSearchResults_FindsSeason2()
    {
        var url = DvdCompare.DvdCompareProvider.ParseSearchResults(SampleSearchHtml, "My Name is Earl", 2);
        Assert.Equal("https://dvdcompare.net/comparisons/film.php?fid=21363", url);
    }

    [Fact]
    public void ParseSearchResults_SeasonNotFound_ReturnsNull()
    {
        var url = DvdCompare.DvdCompareProvider.ParseSearchResults(SampleSearchHtml, "My Name is Earl", 5);
        Assert.Null(url);
    }

    [Fact]
    public void ParseSearchResults_EmptyHtml_ReturnsNull()
    {
        var url = DvdCompare.DvdCompareProvider.ParseSearchResults("<html></html>", "Test", 1);
        Assert.Null(url);
    }

    [Fact]
    public void ParseSearchResults_FuzzyMatchTitle()
    {
        var url = DvdCompare.DvdCompareProvider.ParseSearchResults(SampleSearchHtml, "My Name Is Earl", 1);
        Assert.Equal("https://dvdcompare.net/comparisons/film.php?fid=9923", url);
    }

    [Fact]
    public void ParseSearchResults_NoMatchingTitle_ReturnsNull()
    {
        var url = DvdCompare.DvdCompareProvider.ParseSearchResults(SampleSearchHtml, "Some Other Show", 1);
        Assert.Null(url);
    }

    [Fact]
    public void ParseSearchResults_FallbackToTitleMatch_WhenNoSeasonInfo()
    {
        // Results without "Season N" in the link text — should fall back to title match
        string html =
            "<a href=\"film.php?fid=123\">My Show: The Complete First Season</a>" +
            "<a href=\"film.php?fid=456\">My Show: The Complete Second Season</a>";

        var url = DvdCompare.DvdCompareProvider.ParseSearchResults(html, "My Show", 1);
        // No season info parsed, falls back to first title match
        Assert.Equal("https://dvdcompare.net/comparisons/film.php?fid=123", url);
    }
}
