using ArmRipper.Core.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ArmRipper.WebUi.Controllers;

[Route("api")]
public class ApiController(ArmDbContext db) : Controller
{
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
        var successJobs = await db.Jobs.CountAsync(j => j.Status == ArmRipper.Core.Models.JobState.Success);

        return Json(new
        {
            totalJobs,
            successJobs,
            successRate = totalJobs > 0 ? (double)successJobs / totalJobs * 100 : 0
        });
    }
}
