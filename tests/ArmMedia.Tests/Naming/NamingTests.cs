using ArmMedia.Core.Models;
using ArmMedia.Naming;
using Xunit;

namespace ArmMedia.Tests.Naming;

/// <summary>
/// Unit tests for <see cref="DefaultEpisodeRenamer"/> template processing.
/// </summary>
public sealed class NamingTests
{
    private readonly DefaultEpisodeRenamer _renamer = new();

    private static MappedTrack MakeTrack(
        int trackIndex, int season, int[] episodes,
        string? title = "Episode Title",
        bool isExtra = false) => new()
    {
        TrackIndex      = trackIndex,
        Season          = season,
        Episodes        = episodes,
        Title           = title,
        IsExtra         = isExtra,
        WinningProvider = "DiscDb",
        Confidence      = Confidence.Definitive
    };

    // ─────────────────────────────────────────────────────────────────────────
    // Standard episode naming
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void StandardEpisode_JellyfinTemplate_ProducesCorrectName()
    {
        var track   = MakeTrack(1, 1, [5], "Pilot");
        var opts    = NamingOptions.Jellyfin;
        opts.SeriesTitle = "Cosmic Frontier";

        var result = _renamer.Rename(track, opts);
        Assert.Equal("Cosmic Frontier - S01E05 - Pilot", result);
    }

    [Fact]
    public void StandardEpisode_PlexTemplate_ProducesCorrectPath()
    {
        var track   = MakeTrack(1, 2, [3], "The Setup");
        var opts    = NamingOptions.Plex;
        opts.SeriesTitle = "Galaxy Run";

        var result = _renamer.Rename(track, opts);
        Assert.Equal("Galaxy Run/Season 02/Galaxy Run - S02E03 - The Setup", result);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Multi-part episode naming
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void MultiPartEpisode_JellyfinTemplate_IncludesBothEpisodeNumbers()
    {
        var track   = MakeTrack(2, 1, [3, 4], "The Long Night");
        var opts    = NamingOptions.Jellyfin;
        opts.SeriesTitle = "Cosmic Frontier";

        var result = _renamer.Rename(track, opts);
        // Template: "{Series} - S{Season:D2}{Episodes} - {Title}"
        // Episodes token for [3,4] → "E03E04"
        Assert.Contains("E03E04", result);
        Assert.Contains("Cosmic Frontier", result);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Extras naming
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Extra_UsesExtraTemplate()
    {
        var track   = MakeTrack(6, 0, [0], "Behind the Scenes", isExtra: true);
        var opts    = NamingOptions.Jellyfin;
        opts.SeriesTitle = "Cosmic Frontier";

        var result = _renamer.Rename(track, opts);
        Assert.Equal("Cosmic Frontier - S00 - Behind the Scenes", result);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // File name sanitisation
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void FileNameWithInvalidChars_IsSanitized()
    {
        var track   = MakeTrack(1, 1, [1], "Title: With/Slashes");
        var opts    = new NamingOptions
        {
            SeriesTitle      = "My: Series",
            Template         = "{Series} - S{Season:D2}E{Episode:D2} - {Title}",
            SanitizeFileName = true
        };

        var result = _renamer.Rename(track, opts);
        Assert.DoesNotContain(":", result);
        Assert.DoesNotContain("/", result);
    }

    [Fact]
    public void SanitizationDisabled_PreservesSpecialChars()
    {
        var track   = MakeTrack(1, 1, [1], "Normal Title");
        var opts    = new NamingOptions
        {
            SeriesTitle      = "Series",
            Template         = "{Series} S{Season:D2}E{Episode:D2} {Title}",
            SanitizeFileName = false
        };

        var result = _renamer.Rename(track, opts);
        Assert.Equal("Series S01E01 Normal Title", result);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Null title fallback
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void NullTitle_FallsBackToUnknown()
    {
        var track   = MakeTrack(1, 1, [1], null);
        var opts    = new NamingOptions { SeriesTitle = "S", Template = "{Series} - {Title}" };
        var result  = _renamer.Rename(track, opts);
        Assert.Contains("Unknown", result);
    }
}
