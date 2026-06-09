using System.Text.RegularExpressions;
using System.Xml.Linq;
using ArmRipper.Core.Configuration;
using ArmRipper.Core.Infrastructure;
using ArmRipper.Core.Infrastructure.Data;
using ArmRipper.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArmRipper.Core.Rip;

public sealed partial class MusicBrainzService(
    ICliProcessRunner runner,
    ILogger<MusicBrainzService> logger,
    ArmDbContext db,
    IOptions<ArmSettings> settings,
    HttpClient httpClient) : IMusicBrainzService
{
    public async Task<string> IdentifyAsync(Job job, CancellationToken ct = default)
    {
        var discId = await GetDiscIdAsync(job.DevPath!, ct);
        if (string.IsNullOrEmpty(discId))
            return "";

        if (settings.Value.GetAudioTitle is not null and not "none")
        {
            var title = await MusicBrainzLookupAsync(job, discId, ct);
            if (!string.IsNullOrEmpty(title))
            {
                await db.SaveChangesAsync(ct);
                return title;
            }
        }

        return "";
    }

    private async Task<string?> GetDiscIdAsync(string devPath, CancellationToken ct)
    {
        try
        {
            var result = await runner.RunAsync("discid", devPath, timeoutMs: 15_000, ct: ct);
            if (result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StdOut))
            {
                // discid returns the disc ID on first line
                var lines = result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                return lines.Length > 0 ? lines[0].Trim() : null;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get disc ID from {DevPath}", devPath);
        }

        return null;
    }

    private async Task<string> MusicBrainzLookupAsync(Job job, string discId, CancellationToken ct)
    {
        var xml = await GetDiscInfoAsync(job, discId, ct);
        if (string.IsNullOrEmpty(xml))
            return "";

        var artistTitle = await CheckMusicBrainzData(job, xml, ct);
        return artistTitle;
    }

    private async Task<string?> GetDiscInfoAsync(Job job, string discId, CancellationToken ct)
    {
        try
        {
            var url = $"https://musicbrainz.org/ws/2/discid/{discId}?inc=artist-credits+recordings&fmt=xml";
            var response = await httpClient.GetStringAsync(url, ct);
            return response;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to reach MusicBrainz or CD not found");
            return null;
        }
    }

    private async Task<string> CheckMusicBrainzData(Job job, string xml, CancellationToken ct)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException ex)
        {
            logger.LogError(ex, "Malformed XML from MusicBrainz");
            return "";
        }
        var root = doc.Root;
        if (root is null) return "";

        var ns = root.GetDefaultNamespace();

        // Check for disc data
        var disc = root.Element(ns + "disc");
        var cdstub = root.Element(ns + "cdstub");

        if (disc is null && cdstub is null)
        {
            logger.LogError("No release information reported by MusicBrainz");
            return "";
        }

        string musicData;

        if (disc is not null)
        {
            logger.LogInformation("Processing as a disc");
            musicData = await ProcessDiscRelease(job, disc, ns, ct);
        }
        else
        {
            logger.LogInformation("Processing as a cdstub");
            musicData = ProcessCdStub(job, cdstub!, ns, ct);
        }

        return musicData;
    }

    private async Task<string> ProcessDiscRelease(Job job, XElement disc, XNamespace ns, CancellationToken ct)
    {
        var releaseList = disc.Element(ns + "release-list");
        if (releaseList is null)
        {
            logger.LogError("No release-list in MusicBrainz response");
            return "";
        }

        var releases = releaseList.Elements(ns + "release").ToList();
        logger.LogDebug("Number of releases: {Count}", releases.Count);

        if (releases.Count == 0) return "";

        for (var i = 0; i < releases.Count; i++)
        {
            var release = releases[i];
            var mediumList = release.Element(ns + "medium-list");
            if (mediumList is null) continue;

            var mediums = mediumList.Elements(ns + "medium").ToList();
            if (mediums.Count == 0) continue;

            var format = mediums[0].Element(ns + "format")?.Value;
            if (format != "CD") continue;

            logger.LogInformation("Release [{Index}] is a CD, tracking on...", i);

            var trackList = mediums[0].Element(ns + "track-list");
            if (trackList is not null)
                ProcessTracks(job, trackList, ns, isStub: false);

            var newYear = CheckDate(release, ns);
            var title = release.Element(ns + "title")?.Value ?? "no title";
            var artistCredit = release.Element(ns + "artist-credit");
            var artist = artistCredit?.Element(ns + "name-credit")?.Element(ns + "artist")?.Element(ns + "name")?.Value ?? "Unknown Artist";
            var offsetCount = disc.Element(ns + "offset-count")?.Value;
            var artistTitle = $"{artist} {title}";
            var releaseId = release.Attribute("id")?.Value ?? "";

            logger.LogInformation("CD args: job_id={JobId} crc_id={CrcId} title={Title}", job.Id, releaseId, artistTitle);

            job.CrcId = releaseId;
            job.HasNiceTitle = true;
            job.Year = job.YearAuto = newYear;
            job.Title = job.TitleAuto = artistTitle;
            if (offsetCount is not null && int.TryParse(offsetCount, out var offsetVal))
                job.NoOfTitles = offsetVal;

            // Get CD artwork
            _ = await GetCdArtAsync(job, disc, ns, ct);

            return artistTitle;
        }

        return "";
    }

    private string ProcessCdStub(Job job, XElement cdstub, XNamespace ns, CancellationToken ct)
    {
        var trackList = cdstub.Element(ns + "track-list");
        if (trackList is not null)
            ProcessTracks(job, trackList, ns, isStub: true);

        var title = cdstub.Element(ns + "title")?.Value ?? "";
        var artist = cdstub.Element(ns + "artist")?.Value ?? "Unknown Artist";
        var trackCount = cdstub.Element(ns + "track-count")?.Value;
        var stubId = cdstub.Attribute("id")?.Value ?? "";

        var artistTitle = $"{artist} {title}";

        job.CrcId = stubId;
        job.HasNiceTitle = true;
        job.Year = job.YearAuto = "";
        job.Title = job.TitleAuto = artistTitle;
        if (trackCount is not null && int.TryParse(trackCount, out var tcVal))
            job.NoOfTitles = tcVal;

        logger.LogInformation("cdstub args: job_id={JobId} crc_id={CrcId} title={Title}", job.Id, stubId, artistTitle);

        return artistTitle;
    }

    private static string CheckDate(XElement release, XNamespace ns)
    {
        var date = release.Element(ns + "date")?.Value;
        if (string.IsNullOrEmpty(date))
            return "";

        // Trim to just the year
        return DateYearPattern().Replace(date, "");
    }

    private void ProcessTracks(Job job, XElement trackList, XNamespace ns, bool isStub)
    {
        var tracks = trackList.Elements(ns + "track").ToList();
        foreach (var (track, idx) in tracks.Select((t, i) => (t, i)))
        {
            var trackLeng = 0;
            var lengthStr = isStub
                ? track.Element(ns + "length")?.Value
                : track.Element(ns + "recording")?.Element(ns + "length")?.Value;

            if (lengthStr is not null && int.TryParse(lengthStr, out var parsedLen))
                trackLeng = parsedLen / 1000; // MusicBrainz returns ms

            var trackNo = track.Element(ns + "number")?.Value ?? (idx + 1).ToString();
            var title = isStub
                ? track.Element(ns + "title")?.Value ?? $"Untitled track {trackNo}"
                : track.Element(ns + "recording")?.Element(ns + "title")?.Value ?? $"Untitled track {trackNo}";

            AddTrack(job, trackNo, trackLeng, title);
        }
    }

    private void AddTrack(Job job, string trackNo, int seconds, string title)
    {
        var track = new Track
        {
            JobId = job.Id,
            TrackNumber = trackNo,
            Length = seconds,
            Fps = 0.1,
            Source = "MusicBrainz",
            FileName = title,
            BaseName = title,
            Ripped = seconds > (settings.Value.MinLength)
        };

        db.Tracks.Add(track);
    }

    private async Task<bool> GetCdArtAsync(Job job, XElement disc, XNamespace ns, CancellationToken ct)
    {
        try
        {
            var releaseList = disc.Element(ns + "release-list");
            if (releaseList is null) return false;

            var releases = releaseList.Elements(ns + "release").ToList();

            foreach (var release in releases)
            {
                var coverArt = release.Element(ns + "cover-art-archive");
                var artwork = coverArt?.Element(ns + "artwork")?.Value;
                if (artwork == "false") continue;

                var releaseId = release.Attribute("id")?.Value;
                if (releaseId is null) continue;

                var artUrl = $"https://coverartarchive.org/release/{releaseId}";
                var response = await httpClient.GetAsync(artUrl, ct);
                if (!response.IsSuccessStatusCode) continue;

                var json = await response.Content.ReadAsStringAsync(ct);
                var doc = System.Text.Json.JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("images", out var images)) continue;

                foreach (var image in images.EnumerateArray())
                {
                    if (image.TryGetProperty("image", out var imgUrl))
                    {
                        job.PosterUrl = job.PosterUrlAuto = imgUrl.GetString();
                        return true;
                    }
                }

                break;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get CD artwork");
        }

        return false;
    }

    [GeneratedRegex(@"-\d{2}-\d{2}$")]
    private static partial Regex DateYearPattern();
}
