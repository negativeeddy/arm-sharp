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
    public async Task<IActionResult> JobDetail(int jobId)
    {
        var job = await db.Jobs
            .Include(j => j.Tracks)
            .Include(j => j.Config)
            .FirstOrDefaultAsync(j => j.Id == jobId);

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
    public async Task<IActionResult> ActiveRips()
    {
        var jobs = await db.Jobs
            .Include(j => j.Config)
            .Where(j => j.Status != JobState.Success && j.Status != JobState.Failure && j.Status != JobState.Cancelled)
            .OrderByDescending(j => j.StartTime)
            .ToListAsync();

        return View(jobs);
    }

    [HttpPost("update-identification")]
    public async Task<IActionResult> UpdateIdentification(int jobId, string? title, string? year, string? videoType, string? imdbId, string? posterUrl)
    {
        var job = await db.Jobs.FindAsync(jobId);
        if (job is null)
            return NotFound();

        if (title is not null) { job.TitleManual = title; job.Title = title; }
        if (year is not null) { job.YearManual = year; job.Year = year; }
        if (videoType is not null) { job.VideoTypeManual = videoType; job.VideoType = videoType; }
        if (imdbId is not null) { job.ImdbIdManual = imdbId; job.ImdbId = imdbId; }
        if (posterUrl is not null) { job.PosterUrlManual = posterUrl; job.PosterUrl = posterUrl; }

        await db.SaveChangesAsync();
        return RedirectToAction("JobDetail", new { jobId });
    }

    [HttpPost("set-title")]
    public async Task<IActionResult> SetTitle(int jobId, string title, string? returnUrl = null)
    {
        var job = await db.Jobs.FindAsync(jobId);
        if (job is null)
            return NotFound();

        job.TitleManual = title;
        job.Title = title;
        await db.SaveChangesAsync();

        return Redirect(returnUrl ?? Url.Action("TitleSearch")!);
    }

    [HttpPost("cancel")]
    public async Task<IActionResult> Cancel(int jobId)
    {
        var job = await db.Jobs.FindAsync(jobId);
        if (job is null)
            return NotFound();

        if (!job.Status.IsTerminal())
        {
            job.Status = JobState.Cancelled;
            job.Errors = "Cancelled by user";
            await db.SaveChangesAsync();
        }

        if (!string.IsNullOrEmpty(job.DevPath))
            backgroundRip.CancelRip(job.DevPath);

        return RedirectToAction("JobDetail", new { jobId });
    }

    [HttpGet("titlesearch")]
    public async Task<IActionResult> TitleSearch(string query, int? jobId)
    {
        ViewBag.Jobs = await db.Jobs
            .OrderByDescending(j => j.StartTime)
            .Take(20)
            .ToListAsync();
        ViewBag.SelectedJobId = jobId;

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
    public async Task<IActionResult> SelectTitle(string imdbId, int jobId)
    {
        var job = await db.Jobs.FindAsync(jobId);
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
    public async Task<IActionResult> AssignMovie(int jobId, string title, string? year, string? imdbId, string? posterUrl, string? videoType)
    {
        var job = await db.Jobs.FindAsync(jobId);
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

        await db.SaveChangesAsync();

        return RedirectToAction("JobDetail", new { jobId });
    }

    [HttpPost("continue-wait")]
    public async Task<IActionResult> ContinueWait(int jobId)
    {
        var job = await db.Jobs.FindAsync(jobId);
        if (job is null)
            return NotFound();

        if (job.Status == JobState.ManualWaitStarted)
        {
            job.ManualWaitResume = true;
            await db.SaveChangesAsync();
        }

        return RedirectToAction("JobDetail", new { jobId });
    }
}
