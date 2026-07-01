using ArmMedia.Core.Abstractions;
using ArmMedia.Core.Models;
using ArmRipper.Core.Configuration;
using ArmRipper.Core.Infrastructure.Data;
using ArmRipper.Core.Models;
using ArmRipper.Core.Rip;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ArmRipper.WebUi.Controllers;

[Authorize]
[Route("reidentify")]
public class ReIdentifyController(ArmDbContext db, IEpisodeIdentificationOrchestrator orchestrator, IOptions<ArmSettings> settings) : Controller
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
    public async Task<IActionResult> Run(int jobId, bool save = false, bool renameFiles = false, int? startingEpisodeNumber = null, CancellationToken ct = default)
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

        // ── Compute old file paths (before save) for rename tracking ──
        var oldFilePaths = new Dictionary<int, string?>();
        if (renameFiles)
        {
            foreach (var t in rippedTracks)
            {
                if (t.EpisodeNumber.HasValue)
                {
                    oldFilePaths[t.Id] = BuildEpisodeFilePath(job, t, settings.Value);
                }
            }
        }

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

        // ── Rename completed files if requested ──
        var renameResults = new List<object>();
        if (renameFiles && save)
        {
            foreach (var mapped in episodeMap.Tracks)
            {
                var track = rippedTracks.FirstOrDefault(t => t.TrackNumberInt == mapped.TrackIndex);
                if (track is null)
                    continue;

                // Only rename tracks that have episode data
                if (!mapped.Episodes.Any())
                    continue;

                var oldPath = oldFilePaths.GetValueOrDefault(track.Id);
                if (string.IsNullOrEmpty(oldPath))
                    continue;

                var newPath = BuildEpisodeFilePath(job, track, settings.Value);

                // Skip if the path hasn't changed
                if (string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase))
                {
                    renameResults.Add(new { track.Id, trackIndex = mapped.TrackIndex, status = "unchanged", oldPath, newPath });
                    continue;
                }

                // Perform the rename if the old file exists
                var result = RenameFileOnDisk(oldPath, newPath, mapped.TrackIndex);
                renameResults.Add(result);
            }

            // Try to clean up empty directories after renames
            try
            {
                var completedBase = job.Config?.CompletedPath ?? ArmPaths.GetCompletedPath(settings.Value);
                var cleanSeries = CleanSeriesTitle(job.Title ?? job.Label ?? "Unknown Series");
                var seriesDir = Path.Combine(completedBase, "tv", ArmRipperService.SanitizeFileName(cleanSeries));
                if (Directory.Exists(seriesDir))
                {
                    RemoveEmptyDirectories(seriesDir);
                }
            }
            catch
            {
                // Best-effort cleanup
            }
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
            saved         = save,
            renamed       = renameFiles && save,
            renameResults = renameFiles && save ? renameResults : null
        });
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string CleanSeriesTitle(string title)
        => ArmRipperService.CleanSeriesTitle(title);

    private static int ParseDiscNumber(string? label)
        => ArmRipperService.ParseDiscNumber(label);

    private static string FormatDuration(TimeSpan d)
    {
        if (d.TotalHours >= 1)
            return $"{(int)d.TotalHours}h{d.Minutes:D2}m";
        return $"{d.Minutes}m{d.Seconds:D2}s";
    }

    /// <summary>
    /// Builds the expected file path for a TV-series track, mirroring the logic
    /// in <c>ArmRipperService.MoveFiles</c> for episode naming.
    /// </summary>
    private static string BuildEpisodeFilePath(Job job, Track track, ArmSettings armSettings)
    {
        var season = track.TrackSeasonNumber ?? job.SeasonNumber ?? 1;
        var episode = track.EpisodeNumber!.Value;

        var cleanSeries = ArmRipperService.CleanSeriesTitle(job.Title ?? "Unknown Series");
        var completedBase = job.Config?.CompletedPath ?? ArmPaths.GetCompletedPath(armSettings);
        var seriesFileName = ArmRipperService.SanitizeFileName(cleanSeries);
        var seriesDir = Path.Combine(completedBase, "tv", seriesFileName);
        var seasonDir = Path.Combine(seriesDir, $"Season {season:D2}");

        // IMPORTANT: Must match MoveFiles() exactly — fallback to "mp4" for consistency.
        var destExt = job.Config?.DestExt ?? armSettings.DestExt ?? "mp4";
        var episodeTitle = !string.IsNullOrEmpty(track.EpisodeTitle)
            ? $" - {ArmRipperService.SanitizeFileName(track.EpisodeTitle)}"
            : "";

        return Path.Combine(seasonDir,
            $"{seriesFileName} - S{season:D2}E{episode:D2}{episodeTitle}.{destExt}");
    }

    /// <summary>
    /// Renames a file on disk from <paramref name="oldPath"/> to <paramref name="newPath"/>.
    /// Returns a result object with status information.
    /// </summary>
    private static object RenameFileOnDisk(string oldPath, string newPath, int trackIndex)
    {
        if (!System.IO.File.Exists(oldPath))
        {
            return new
            {
                trackIndex,
                oldPath,
                newPath,
                status = "not_found",
                message = $"Old file not found on disk: {oldPath}"
            };
        }

        if (string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase))
        {
            return new { trackIndex, oldPath, newPath, status = "unchanged", message = "" };
        }

        // Ensure the target directory exists
        var dir = Path.GetDirectoryName(newPath);
        if (dir is not null && !Directory.Exists(dir))
        {
            try { Directory.CreateDirectory(dir); } catch { }
        }

        // Check if destination already exists
        if (System.IO.File.Exists(newPath))
        {
            return new
            {
                trackIndex,
                oldPath,
                newPath,
                status = "skipped",
                message = $"Destination already exists: {newPath}"
            };
        }

        try
        {
            System.IO.File.Move(oldPath, newPath);
            return new
            {
                trackIndex,
                oldPath,
                newPath,
                status = "renamed",
                message = ""
            };
        }
        catch (Exception ex)
        {
            return new
            {
                trackIndex,
                oldPath,
                newPath,
                status = "error",
                message = ex.Message
            };
        }
    }

    /// <summary>Recursively removes empty directories under the given path.</summary>
    private static void RemoveEmptyDirectories(string directory)
    {
        if (!Directory.Exists(directory))
            return;

        foreach (var subDir in Directory.GetDirectories(directory))
        {
            RemoveEmptyDirectories(subDir);
        }

        // Only remove if the directory is a season folder or series folder and is empty
        var name = Path.GetFileName(directory);
        if ((name.StartsWith("Season ", StringComparison.OrdinalIgnoreCase) || !Directory.GetFileSystemEntries(directory).Any()))
        {
            try { Directory.Delete(directory); } catch { }
        }
    }
}
