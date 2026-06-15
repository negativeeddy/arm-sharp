using System.Reflection;
using ArmRipper.Core.Configuration;
using ArmRipper.Core.Infrastructure;
using ArmRipper.Core.Infrastructure.Data;
using ArmRipper.Core.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ArmRipper.WebUi.Controllers;

[Authorize]
[Route("api")]
public partial class ApiController(
    ArmDbContext db,
    ICliProcessRunner runner,
    IOptions<ArmSettings> settings) : Controller
{
    [HttpGet("health")]
    public IActionResult Health()
    {
        var version = Assembly.GetEntryAssembly()?.GetName()?.Version?.ToString()
            ?? typeof(ApiController).Assembly.GetName().Version?.ToString()
            ?? "0.0.0";

        return Json(new
        {
            status = "healthy",
            timestamp = DateTime.UtcNow.ToString("o"),
            version
        });
    }
    [HttpGet("jobs")]
    public async Task<IActionResult> GetJobs()
    {
        var jobs = await db.Jobs
            .OrderByDescending(j => j.StartTime)
            .Take(20)
            .Select(j => new
            {
                j.Id,
                j.Title,
                j.Year,
                j.VideoType,
                j.DiscType,
                j.Status,
                j.StartTime,
                j.StopTime,
                j.Path
            })
            .ToListAsync();

        return Json(jobs);
    }

    [HttpGet("jobs/active")]
    public async Task<IActionResult> ActiveJobs()
    {
        var jobs = await db.Jobs
            .Include(j => j.Config)
            .Where(j => j.Status != JobState.Success && j.Status != JobState.Failure && j.Status != JobState.Cancelled)
            .OrderByDescending(j => j.StartTime)
            .ToListAsync();

        return PartialView("~/Views/Jobs/_ActiveJobRows.cshtml", jobs);
    }

    [HttpGet("jobs/{id:int}/pipeline")]
    public async Task<IActionResult> JobPipeline(int id)
    {
        var job = await db.Jobs.FirstOrDefaultAsync(j => j.Id == id);
        if (job is null)
            return NotFound();
        return PartialView("~/Views/Shared/_Pipeline.cshtml", job);
    }

    [HttpGet("jobs/{id}")]
    public async Task<IActionResult> GetJob(int id)
    {
        var job = await db.Jobs
            .Include(j => j.Tracks)
            .Include(j => j.Config)
            .FirstOrDefaultAsync(j => j.Id == id);

        if (job is null)
            return NotFound();

        return Json(job);
    }

    [HttpGet("drives")]
    public async Task<IActionResult> GetDrives()
    {
        var drives = await db.SystemDrives.ToListAsync();
        return Json(drives);
    }

    [HttpGet("stats")]
    public async Task<IActionResult> GetStats()
    {
        var totalJobs = await db.Jobs.CountAsync();
        var successJobs = await db.Jobs.CountAsync(j => j.Status == JobState.Success);

        return Json(new
        {
            totalJobs,
            successJobs,
            successRate = totalJobs > 0 ? (double)successJobs / totalJobs * 100 : 0
        });
    }

    [HttpPost("abandon/{id}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Abandon(int id)
    {
        var job = await db.Jobs.Include(j => j.Config).FirstOrDefaultAsync(j => j.Id == id);
        if (job is null)
            return NotFound(new { success = false, error = "Job not found" });

        if (job.Pid is int pid)
        {
            try
            {
                var process = System.Diagnostics.Process.GetProcessById(pid);
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            catch (Exception)
            {
                // Process already gone — fine
            }
        }

        job.Status = JobState.Failure;
        job.Errors = "Abandoned by user";

        if (job.DevPath is not null)
        {
            try
            {
                await runner.RunAsync("umount", job.DevPath, timeoutMs: 10_000);
                await runner.RunAsync("eject", job.DevPath, timeoutMs: 10_000);
            }
            catch { }
        }

        await db.SaveChangesAsync();
        return Json(new { success = true, job = id, mode = "abandon" });
    }

    [HttpPost("change-params")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ChangeParams(
        int jobId, string? disctype = null, int? minLength = null,
        int? maxLength = null, string? ripMethod = null, bool? mainFeature = null)
    {
        var job = await db.Jobs.Include(j => j.Config).FirstOrDefaultAsync(j => j.Id == jobId);
        if (job?.Config is null)
            return NotFound(new { success = false, error = "Job or config not found" });

        var config = job.Config;

        if (disctype is not null && Enum.TryParse<DiscType>(disctype, true, out var dt))
            job.DiscType = dt;
        if (minLength.HasValue)
            config.MinLength = minLength;
        if (maxLength.HasValue)
            config.MaxLength = maxLength;
        if (ripMethod is not null)
            config.RipMethod = ripMethod;
        if (mainFeature.HasValue)
            config.MainFeature = mainFeature.Value;

        await db.SaveChangesAsync();
        return Json(new
        {
            success = true,
            message = $"Parameters changed. Rip Method={config.RipMethod}, " +
                      $"Main Feature={config.MainFeature}, " +
                      $"Min Length={config.MinLength}, Max Length={config.MaxLength}, " +
                      $"Disctype={job.DiscType}"
        });
    }

    [HttpGet("log")]
    public async Task<IActionResult> GetLog(int jobId)
    {
        var job = await db.Jobs.FindAsync(jobId);
        if (job?.LogFile is null)
            return Json(new { success = false, error = "Job or log file not found" });

        var logPath = settings.Value.LogPath ?? "/home/arm/logs";
        var fullPath = Path.Combine(logPath, job.LogFile);

        if (!System.IO.File.Exists(fullPath))
            return Json(new { success = false, error = "Log file not found" });

        var logContent = await System.IO.File.ReadAllTextAsync(fullPath);
        return Json(new { success = true, job = jobId, log = logContent });
    }

    /// <summary>
    /// Signal the Conductor to exit the manual wait loop early.
    /// If 'title' is provided, also set TitleManual.
    /// </summary>
    [HttpPost("jobs/{id}/continue")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ContinueManualWait(int id, string? title = null)
    {
        var job = await db.Jobs.FindAsync(id);
        if (job is null)
            return NotFound(new { success = false, error = "Job not found" });

        if (job.Status != JobState.ManualWaitStarted)
            return Json(new { success = false, error = "Job is not in manual wait state" });

        if (title is not null)
        {
            job.TitleManual = title;
            job.Title = title;
        }

        job.ManualWaitResume = true;
        await db.SaveChangesAsync();

        return Json(new { success = true, job = id, message = "Manual wait will resume" });
    }
}
