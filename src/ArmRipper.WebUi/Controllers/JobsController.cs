using ArmRipper.Core.Configuration;
using ArmRipper.Core.Infrastructure;
using ArmRipper.Core.Infrastructure.Data;
using ArmRipper.Core.Metadata;
using ArmRipper.Core.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ArmRipper.WebUi.Controllers;

[Authorize]
[Route("jobs")]
public class JobsController(ArmDbContext db, OmdbService omdb, IOptions<ArmSettings> settings, IBackgroundRipService backgroundRip) : Controller
{
    [HttpGet("jobdetail")]
    public async Task<IActionResult> JobDetail(int jobId, CancellationToken ct = default)
    {
        var job = await db.Jobs
            .Include(j => j.Tracks)
            .Include(j => j.Config)
            .FirstOrDefaultAsync(j => j.Id == jobId, ct);

        if (job is null)
            return NotFound();

        if (!string.IsNullOrEmpty(job.ImdbId) && job.Config?.OmdbApiKey is { Length: > 0 } apiKey)
        {
            try
            {
                var metadata = await omdb.LookupByImdbAsync(job.ImdbId, apiKey, plot: "full");
                ViewBag.Metadata = metadata;
            }
            catch { /* non-critical */ }
        }

        return View(job);
    }

    [HttpGet("activerips")]
    public async Task<IActionResult> ActiveRips(CancellationToken ct = default)
    {
        var activeJobs = await db.Jobs
            .Include(j => j.Config)
            .Where(j => j.Status != JobState.Success
                     && j.Status != JobState.Failure
                     && j.Status != JobState.Cancelled
                     && j.Status != JobState.Stopping)
            .OrderByDescending(j => j.StartTime)
            .ToListAsync(ct);

        // Also fetch stopped/resumable jobs to show in a separate section
        var resumableJobs = await db.Jobs
            .Include(j => j.Config)
            .Where(j => j.Status == JobState.Stopping
                     && !string.IsNullOrEmpty(j.CompletedStages))
            .OrderByDescending(j => j.StartTime)
            .ToListAsync(ct);

        ViewBag.ResumableJobs = resumableJobs;

        return View(activeJobs);
    }

    [HttpPost("update-identification")]
    public async Task<IActionResult> UpdateIdentification(int jobId, string? title, string? year, string? videoType, string? imdbId, string? posterUrl, CancellationToken ct = default)
    {
        var job = await db.Jobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null)
            return NotFound();

        if (title is not null) { job.TitleManual = title; job.Title = title; }
        if (year is not null) { job.YearManual = year; job.Year = year; }
        if (videoType is not null) { job.VideoTypeManual = videoType; job.VideoType = videoType; }
        if (imdbId is not null) { job.ImdbIdManual = imdbId; job.ImdbId = imdbId; }
        if (posterUrl is not null) { job.PosterUrlManual = posterUrl; job.PosterUrl = posterUrl; }

        job.HasNiceTitle = true;

        await db.SaveChangesAsync(ct);
        return RedirectToAction("JobDetail", new { jobId });
    }

    [HttpPost("set-title")]
    public async Task<IActionResult> SetTitle(int jobId, string title, string? returnUrl = null, CancellationToken ct = default)
    {
        var job = await db.Jobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null)
            return NotFound();

        job.TitleManual = title;
        job.Title = title;
        job.HasNiceTitle = true;
        await db.SaveChangesAsync(ct);

        return Redirect(returnUrl ?? Url.Action("TitleSearch")!);
    }

    [HttpGet("log-tail")]
    public async Task<IActionResult> LogTail(int jobId, int lines = 50, CancellationToken ct = default)
    {
        var job = await db.Jobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null)
            return NotFound();

        var logPath = Path.Combine(
            job.Config?.LogPath ?? ArmPaths.GetLogPath(settings.Value),
            job.LogFile ?? $"{jobId}.log");

        if (!System.IO.File.Exists(logPath))
            return Content("");

        // Read last ~lines from the log file (approximate by byte offset)
        var fileInfo = new System.IO.FileInfo(logPath);
        var bufferSize = Math.Min(fileInfo.Length, lines * 512); // ~512 bytes per line avg
        var maxBytes = Math.Min(fileInfo.Length, 64 * 1024); // 64KB max
        var readSize = Math.Min(bufferSize, maxBytes);

        var content = "";
        await using (var fs = new System.IO.FileStream(logPath, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite))
        {
            if (fileInfo.Length > readSize)
                fs.Seek(-readSize, System.IO.SeekOrigin.End);
            using var reader = new System.IO.StreamReader(fs);
            content = await reader.ReadToEndAsync(ct);
        }

        // Trim to last N lines
        var allLines = content.Split('\n');
        var tailLines = allLines.Skip(Math.Max(0, allLines.Length - lines - 1));

        return Content(string.Join('\n', tailLines), "text/plain");
    }

    [HttpPost("cancel")]
    public async Task<IActionResult> Cancel(int jobId, CancellationToken ct = default)
    {
        var job = await db.Jobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null)
            return NotFound();

        if (!job.Status.IsTerminal())
        {
            var prevStatus = job.Status;
            job.Status = JobState.Cancelled;
            job.Errors = "Cancelled by user";
            job.ProgressMessage = "Cancelled";
            await db.SaveChangesAsync(ct);

            // Write to job log file
            AppendToJobLog(job, $"Job cancelled by user (previous status: {prevStatus})");
        }

        if (!string.IsNullOrEmpty(job.DevPath))
            backgroundRip.CancelRip(job.DevPath);

        return RedirectToAction("JobDetail", new { jobId });
    }

    [HttpPost("resume")]
    public IActionResult Resume(int jobId)
    {
        var job = db.Jobs
            .Include(j => j.Config)
            .FirstOrDefault(j => j.Id == jobId);

        if (job is null)
            return NotFound();

        if (!job.Status.IsResumable())
        {
            TempData["Message"] = $"Job {jobId} is not in a resumable state (current: {job.Status})";
            return RedirectToAction("JobDetail", new { jobId });
        }

        if (string.IsNullOrEmpty(job.CompletedStages))
        {
            TempData["Message"] = $"Job {jobId} has no completed stages — cannot determine where to resume";
            return RedirectToAction("JobDetail", new { jobId });
        }

        var prevStatus = job.Status;

        // Reset to Active so the pipeline can proceed
        job.Status = JobState.Active;
        job.StopTime = null;
        job.Errors = null;
        job.ProgressMessage = "Resuming from checkpoint...";
        db.SaveChanges();

        // Write to job log file
        AppendToJobLog(job, $"Job resumed by user (previous status: {prevStatus}, completed stages: {job.CompletedStages})");

        // Kick off a new background rip — the Conductor will skip completed stages
        var devPath = job.DevPath ?? $"resume-{jobId}";
        backgroundRip.StartRip(devPath);

        TempData["Message"] = $"Job {jobId} resumed — picking up from completed stages: {job.CompletedStages}";
        return RedirectToAction("JobDetail", new { jobId });
    }

    [HttpGet("titlesearch")]
    public async Task<IActionResult> TitleSearch(string query, int? jobId, CancellationToken ct = default)
    {
        ViewBag.Jobs = await db.Jobs
            .OrderByDescending(j => j.StartTime)
            .Take(20)
            .ToListAsync(ct);
        ViewBag.SelectedJobId = jobId;

        // If redirected from the Completed page with an import file path, pass it to the view
        // so search results display an "Import & Transcode" button.
        var importFilePath = TempData["ImportFilePath"] as string;
        if (!string.IsNullOrEmpty(importFilePath))
        {
            ViewBag.ImportFilePath = importFilePath;
            // Persist for the POST that follows
            TempData.Keep("ImportFilePath");
        }

        if (string.IsNullOrWhiteSpace(query))
            return View(Array.Empty<OmdbSearchItem>());

        var apiKey = settings.Value.OmdbApiKey;
        if (!string.IsNullOrEmpty(apiKey))
        {
            var result = await omdb.SearchAsync(apiKey, query);
            if (result?.Search is { Count: > 0 })
                return View(result.Search);
        }

        return View(Array.Empty<OmdbSearchItem>());
    }

    [HttpGet("select-title")]
    public async Task<IActionResult> SelectTitle(string imdbId, int jobId, CancellationToken ct = default)
    {
        var job = await db.Jobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null)
            return NotFound();

        var apiKey = settings.Value.OmdbApiKey;
        if (string.IsNullOrEmpty(apiKey))
        {
            TempData["Error"] = "OMDB API key not configured";
            return RedirectToAction("TitleSearch", new { jobId });
        }

        var result = await omdb.LookupByImdbAsync(imdbId, apiKey, plot: "full");
        if (result is null || result.Response != "True")
        {
            TempData["Error"] = "Could not fetch movie details";
            return RedirectToAction("TitleSearch", new { jobId });
        }

        ViewBag.JobId = jobId;
        return View(result);
    }

    [HttpPost("assign-movie")]
    public async Task<IActionResult> AssignMovie(int jobId, string title, string? year, string? imdbId, string? posterUrl, string? videoType, CancellationToken ct = default)
    {
        var job = await db.Jobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null)
            return NotFound();

        job.TitleManual = title;
        job.Title = title;
        job.YearManual = year;
        job.Year = year;
        job.ImdbIdManual = imdbId;
        job.ImdbId = imdbId;
        job.PosterUrlManual = posterUrl;
        job.PosterUrl = posterUrl;
        if (!string.IsNullOrEmpty(videoType))
        {
            job.VideoTypeManual = videoType;
            job.VideoType = videoType;
        }

        job.HasNiceTitle = true;

        await db.SaveChangesAsync(ct);

        return RedirectToAction("JobDetail", new { jobId });
    }

    /// <summary>
    /// Creates a standalone import transcode job from a movie search result and a raw file path.
    /// Used when the user has raw MKV files (ripped elsewhere) and selects a movie from the
    /// TitleSearch page to identify them.
    /// </summary>
    [HttpPost("import-from-search")]
    public IActionResult ImportFromSearch(string title, string? year, string? videoType, string? discType, string? filePath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            TempData["ErrorMessage"] = "Movie title is required.";
            return RedirectToAction("TitleSearch");
        }

        if (string.IsNullOrEmpty(filePath))
        {
            // Try TempData as fallback (set by CompletedController redirect)
            filePath = TempData["ImportFilePath"] as string;
        }

        if (string.IsNullOrEmpty(filePath) || !System.IO.File.Exists(filePath))
        {
            TempData["ErrorMessage"] = "Raw file not found. Please go back to the Completed page and select a file first.";
            return RedirectToAction("TitleSearch");
        }

        backgroundRip.StartImportJob(filePath, title, year, videoType ?? "movie", discType, ct);

        TempData["SuccessMessage"] = $"Import transcode started for \"{title}\" – job queued in the background.";
        return RedirectToAction("Index", "Home");
    }

    [HttpPost("continue-wait")]
    public async Task<IActionResult> ContinueWait(int jobId, CancellationToken ct = default)
    {
        var job = await db.Jobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null)
            return NotFound();

        if (job.Status == JobState.ManualWaitStarted)
        {
            job.ManualWaitResume = true;
            await db.SaveChangesAsync(ct);
        }

        return RedirectToAction("JobDetail", new { jobId });
    }

    /// <summary>Append a timestamped line to the job's log file.</summary>
    private static void AppendToJobLog(Job job, string message)
    {
        try
        {
            var logPath = job.GetLogFilePath();
            var dir = Path.GetDirectoryName(logPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            var timestamp = DateTime.Now.ToString("HH:mm:ss");
            var entry = $"[{timestamp}] INFO: JobsController: {message}";
            System.IO.File.AppendAllText(logPath, entry + Environment.NewLine);
        }
        catch (Exception ex)
        {
            // Best-effort — non-critical failure
            Console.Error.WriteLine($"Failed to write to job log file: {ex.Message}");
        }
    }
}
