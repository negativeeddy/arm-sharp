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
public class ReIdentifyController(ArmDbContext db, IEpisodeIdentificationOrchestrator orchestrator, ISettingsService settingsService) : Controller
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
    public async Task<IActionResult> Run(int jobId, bool save = false, bool renameFiles = false, int? startingEpisodeNumber = null, int? seasonOverride = null, CancellationToken ct = default)
    {
        var job = await db.Jobs
            .Include(j => j.Tracks)
            .Include(j => j.Config)
            .FirstOrDefaultAsync(j => j.Id == jobId, ct);

        if (job is null)
            return Json(new { error = "Job not found." });

        if (job.Status != JobState.Success && job.Status != JobState.Failure)
            return Json(new { error = "Job is not completed; only completed jobs can be re-identified." });

        // Only process series/tv jobs
        if (job.VideoType is not VideoContentType.Series and not VideoContentType.Tv)
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
            Season                = seasonOverride ?? job.SeasonNumber ?? 1,
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

        // ── Locate the OLD on-disk file for every track (before save mutates track fields) ──
        // A completed job's files can live in very different places depending on whether the
        // original rip had episode mappings:
        //   • Identified rip → {completed}/tv/{Series}/Season {NN}/SxxExx...
        //   • Unidentified rip → {completed}/unidentified/{JobTitle}/{rawName}.mkv
        // We record the ACTUAL existing file so "Save & Rename" can move it to the newly
        // identified episode path. Effective settings are used (not the static IOptions values,
        // which in dev resolve to ./data/... and a null DestExt → "mp4").
        var effectiveSettings = await settingsService.GetEffectiveAsync(ct);
        var oldFilePaths = new Dictionary<int, (string? Found, string[] Candidates)>();
        if (renameFiles)
        {
            foreach (var t in rippedTracks)
            {
                oldFilePaths[t.Id] = LocateCurrentFile(job, t, effectiveSettings);
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
            var completedBase = job.Config?.CompletedPath ?? ArmPaths.GetCompletedPath(effectiveSettings);

            foreach (var mapped in episodeMap.Tracks)
            {
                var track = rippedTracks.FirstOrDefault(t => t.TrackNumberInt == mapped.TrackIndex);
                if (track is null)
                    continue;

                // Only rename tracks that have episode data
                if (!mapped.Episodes.Any())
                    continue;

                var (oldPath, candidates) = oldFilePaths.GetValueOrDefault(track.Id);

                if (string.IsNullOrEmpty(oldPath))
                {
                    renameResults.Add(new
                    {
                        track.Id,
                        trackIndex = mapped.TrackIndex,
                        status = "not_found",
                        oldPath = candidates.FirstOrDefault() ?? "?",
                        newPath = BuildEpisodeFilePath(job, track, effectiveSettings),
                        message = "Old file not found on disk. Searched: " + string.Join(" | ", candidates)
                    });
                    continue;
                }

                var newPath = BuildEpisodeFilePath(job, track, effectiveSettings);

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

            // Try to clean up empty directories after renames (tv series folder)
            try
            {
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

            // Also clean up the unidentified folder the raw files were moved out of
            try
            {
                var unidentifiedDir = Path.Combine(completedBase, "unidentified");
                if (Directory.Exists(unidentifiedDir))
                {
                    RemoveEmptyDirectories(unidentifiedDir);
                }
            }
            catch
            {
                // Best-effort cleanup
            }
        }

        return Json(new
        {
            jobId             = job.Id,
            title             = job.Title,
            season            = seasonOverride ?? job.SeasonNumber,
            originalSeason    = job.SeasonNumber,
            seasonOverridden  = seasonOverride.HasValue && seasonOverride != job.SeasonNumber,
            discLabel         = job.Label,
            videoType         = job.VideoType.ToString(),
            trackCount        = rippedTracks.Count,
            startingEpisodeNumber,
            comparison,
            saved             = save,
            renamed           = renameFiles && save,
            renameResults     = renameFiles && save ? renameResults : null
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

        // Jellyfin convention: series name is in the directory, not the filename
        return Path.Combine(seasonDir,
            $"S{season:D2}E{episode:D2}{episodeTitle}.{destExt}");
    }

    /// <summary>
    /// Finds where a track's ripped file currently lives on disk so the
    /// re-identification rename can move it to the newly identified episode path.
    ///
    /// A completed job's files can be in one of two very different places:
    ///   • Identified rip → {completed}/tv/{Series}/Season {NN}/SxxExx... (track has an episode number)
    ///   • Unidentified rip → {completed}/unidentified/{JobTitle}/{rawName}.mkv (raw file, no episode number)
    /// Both locations are probed (the first that exists wins).
    /// </summary>
    private static (string? Found, string[] Candidates) LocateCurrentFile(Job job, Track track, ArmSettings settings)
    {
        var candidates = new List<string>();
        var rawName = track.FileName ?? track.OrigFileName;

        // 1. If the track previously had an episode number, the file may already be
        //    at the expected tv-series path under the old SxxExx name.
        if (track.EpisodeNumber.HasValue)
        {
            candidates.Add(BuildEpisodeFilePath(job, track, settings));
        }

        // 2. Unidentified-rip fallback: the raw file still sitting in the job's own
        //    final directory (job.Path) or under {completed}/unidentified/.
        if (!string.IsNullOrEmpty(rawName))
        {
            var completedBase = job.Config?.CompletedPath ?? ArmPaths.GetCompletedPath(settings);

            // 2a. Directly under the job's recorded final path (e.g. an unidentified folder).
            if (!string.IsNullOrEmpty(job.Path))
            {
                candidates.Add(Path.Combine(job.Path, rawName));
            }

            // 2b. Under {completed}/unidentified/ — the folder is named after the job
            //     title (with a year suffix when present) or the disc label.
            var title = job.Title?.Trim();
            if (!string.IsNullOrEmpty(title))
            {
                if (!string.IsNullOrEmpty(job.Year) && job.Year != "0000")
                {
                    candidates.Add(Path.Combine(completedBase, "unidentified",
                        ArmRipperService.SanitizeFileName($"{title} ({job.Year})"), rawName));
                }
                candidates.Add(Path.Combine(completedBase, "unidentified",
                    ArmRipperService.SanitizeFileName(title), rawName));
            }
            if (!string.IsNullOrEmpty(job.Label))
            {
                candidates.Add(Path.Combine(completedBase, "unidentified",
                    ArmRipperService.SanitizeFileName(job.Label), rawName));
            }
        }

        var distinct = candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return (distinct.FirstOrDefault(System.IO.File.Exists), distinct);
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
