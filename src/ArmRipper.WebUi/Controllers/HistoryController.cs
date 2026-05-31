using ArmRipper.Core.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ArmRipper.WebUi.Controllers;

[Route("history")]
public class HistoryController(ArmDbContext db) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        var jobs = await db.Jobs
            .Include(j => j.Tracks)
            .OrderByDescending(j => j.StartTime)
            .Take(100)
            .ToListAsync();

        return View(jobs);
    }
}
