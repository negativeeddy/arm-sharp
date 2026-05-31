using ArmRipper.Core.Infrastructure.Data;
using ArmRipper.Core.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ArmRipper.WebUi.Controllers;

[Route("jobs")]
public class JobsController(ArmDbContext db) : Controller
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

        return View(job);
    }

    [HttpGet("activerips")]
    public async Task<IActionResult> ActiveRips()
    {
        var jobs = await db.Jobs
            .Include(j => j.Config)
            .Where(j => j.Status != JobState.Success && j.Status != JobState.Failure)
            .OrderByDescending(j => j.StartTime)
            .ToListAsync();

        return View(jobs);
    }

    [HttpGet("titlesearch")]
    public async Task<IActionResult> TitleSearch(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            var recent = await db.Jobs
                .OrderByDescending(j => j.StartTime)
                .Take(20)
                .ToListAsync();

            return View(recent);
        }

        var results = await db.Jobs
            .Where(j => j.Title != null && j.Title.Contains(query))
            .OrderByDescending(j => j.StartTime)
            .Take(50)
            .ToListAsync();

        return View(results);
    }
}
