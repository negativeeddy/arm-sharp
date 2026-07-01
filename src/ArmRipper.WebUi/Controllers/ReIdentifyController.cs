using System.Text.RegularExpressions;
using ArmMedia.Core.Abstractions;
using ArmMedia.Core.Models;
using ArmRipper.Core.Infrastructure.Data;
using ArmRipper.Core.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ArmRipper.WebUi.Controllers;

[Authorize]
[Route("reidentify")]
public class ReIdentifyController(ArmDbContext db, IEpisodeIdentificationOrchestrator orchestrator) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct = default)
    {
        var completedJobs = await db.Jobs
            .Where(j => j.Status == JobState.Success || j.Status == JobState.Failure)
            .OrderByDescending(j => j.Id)
            .Select(j => new { j.Id, j.Title, j.VideoType, j.Label, j.SeasonNumber })
            .ToListAsync(ct);

        ViewBag.CompletedJobs = completedJobs;
        return View();
    }

    [HttpPost("run")]
    public async Task<IActionResult> Run(int jobId, bool save = false, int? startingEpisodeNumber = null, CancellationToken ct = default)
    {
        var job = await db.Jobs
            .Include(j => j.Tracks)
            .FirstOrDefaultAsync(j => j.Id == jobId, ct);

        if (job is null)
            return Json(new { error = "Job not found." });

        if (job.Status != JobState.Success && job.Status != JobState.Failure)
            return Json(new { error = "Job is not completed; only completed jobs can be re-identified." });

        // Only process series/tv jobs
        if (job.VideoType != "series" && job.VideoType != "tv")
            return Json(new { error = $"Job is not a TV series (type={job.VideoType})." });

        var rippedTracks = job.Tracks
            .Where(t => t.Ripped)
            .OrderBy(t => t.TrackNumberInt ?? 0)
            .ToList();

        if (rippedTracks.Count == 0)
            return Json(new { error = "Job has no ripped tracks." });

        // ── Old state (before re-identification) ──
        var oldTracks = rippedTracks.Select(t => new
        {
            trackIndex  = t.TrackNumberInt ?? 0,
            fileName    = t.FileName ?? t.OrigFileName ?? $"Track {t.TrackNumber}",
            duration    = t.Length is not null ? FormatDuration(TimeSpan.FromSeconds(t.Length.Value)) : "—",
            oldEpisode  = t.EpisodeNumber,
            oldSeason   = t.TrackSeasonNumber,
            oldTitle    = t.EpisodeTitle
        }).ToList();

        // ── Build DiscContext ──
        var trackContexts = rippedTracks.Select(t =>
        {
            var rawProps = new Dictionary<string, string>();
            if (!string.IsNullOrEmpty(t.FileName)) rawProps["FileName"] = t.FileName;
            if (!string.IsNullOrEmpty(t.TrackNumber)) rawProps["TrackNumber"] = t.TrackNumber;
            return new TrackContext
            {
                TrackIndex    = t.TrackNumberInt ?? 0,
                Duration      = TimeSpan.FromSeconds(t.Length ?? 0),
                SizeBytes     = t.FileSize ?? 0,
                ChapterCount  = t.Chapters,
                DiscDbTrackId = t.DiscDbItemSlug,
                RawProperties = rawProps
            };
        }).ToList().AsReadOnly();

        var seriesTitle = CleanSeriesTitle(job.Title ?? job.Label ?? "Unknown");
        var discNumber  = ParseDiscNumber(job.Label);

        var ctx = new DiscContext
        {
            DiscId                = job.DiscDbHash ?? job.Label ?? job.DevPath ?? "unknown",
            SeriesTitle           = seriesTitle,
            Season                = job.SeasonNumber ?? 1,
            Tracks                = trackContexts,
            DiscNumber            = discNumber,
            StartingEpisodeNumber = startingEpisodeNumber
        };

        // ── Run identification ──
        var episodeMap = await orchestrator.IdentifyAsync(ctx, ct);

        // ── New state ──
        var newTracks = episodeMap.Tracks
            .OrderBy(t => t.TrackIndex)
            .Select(t => new
            {
                t.TrackIndex,
                season      = t.Season,
                episodes    = t.Episodes,
                title       = t.Title,
                isExtra     = t.IsExtra,
                isMultiPart = t.IsMultiPart,
                provider    = t.WinningProvider,
                confidence  = t.Confidence.ToString(),
                display     = t.IsExtra
                    ? $"S00E{t.Episodes.FirstOrDefault():D2}"
                    : $"S{t.Season:D2}E{t.Episodes.FirstOrDefault():D2}"
            })
            .ToList();

        // ── Build comparison ──
        var comparison = oldTracks.Select(o =>
        {
            var n = newTracks.FirstOrDefault(x => x.TrackIndex == o.trackIndex);
            return new
            {
                o.trackIndex,
                o.fileName,
                o.duration,
                oldEpisodeNumber = o.oldEpisode,
                oldSeasonNumber  = o.oldSeason,
                oldTitle         = o.oldTitle,
                oldDisplay       = o.oldEpisode.HasValue
                    ? (o.oldSeason.HasValue ? $"S{o.oldSeason:D2}E{o.oldEpisode:D2}" : $"E{o.oldEpisode:D2}")
                    : "—",
                newEpisodeNumber = n?.episodes?.FirstOrDefault(),
                newSeasonNumber  = n?.season,
                newTitle         = n?.title,
                newDisplay       = n?.display ?? "—",
                changed  = o.oldEpisode != n?.episodes?.FirstOrDefault()
                        || o.oldSeason != n?.season
                        || o.oldTitle != n?.title,
                provider = n?.provider,
                confidence = n?.confidence
            };
        }).ToList();

        // ── Save if requested ──
        if (save)
        {
            foreach (var mapped in episodeMap.Tracks)
            {
                var track = rippedTracks.FirstOrDefault(t => t.TrackNumberInt == mapped.TrackIndex);
                if (track is not null)
                {
                    track.EpisodeNumber     = mapped.Episodes.Length > 0 ? mapped.Episodes[0] : null;
                    track.EpisodeTitle      = mapped.Title;
                    track.TrackSeasonNumber = mapped.Season;
                }
            }
            await db.SaveChangesAsync(ct);
        }

        return Json(new
        {
            jobId         = job.Id,
            title         = job.Title,
            season        = job.SeasonNumber,
            discLabel     = job.Label,
            videoType     = job.VideoType,
            trackCount    = rippedTracks.Count,
            startingEpisodeNumber,
            comparison,
            saved         = save
        });
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string CleanSeriesTitle(string title)
    {
        var cleaned = Regex.Replace(title, @"_S\d+_D\d+$", "", RegexOptions.IgnoreCase);
        cleaned = cleaned.Replace('_', ' ');
        var words = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < words.Length; i++)
        {
            if (words[i].Length > 0)
                words[i] = char.ToUpperInvariant(words[i][0]) + words[i][1..].ToLowerInvariant();
        }
        return string.Join(' ', words);
    }

    private static int ParseDiscNumber(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
            return 1;
        var match = Regex.Match(label, @"_D(\d+)$", RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups[1].Value, out var discNum) ? discNum : 1;
    }

    private static string FormatDuration(TimeSpan d)
    {
        if (d.TotalHours >= 1)
            return $"{(int)d.TotalHours}h{d.Minutes:D2}m";
        return $"{d.Minutes}m{d.Seconds:D2}s";
    }
}
