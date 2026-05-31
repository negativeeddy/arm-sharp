using ArmRipper.Core.Infrastructure.Data;
using ArmRipper.Core.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ArmRipper.WebUi.Controllers;

[Route("")]
public class HomeController(ArmDbContext db) : Controller
{
    [HttpGet("")]
    [HttpGet("index")]
    public async Task<IActionResult> Index()
    {
        var activeRips = await db.Jobs
            .Include(j => j.Config)
            .Where(j => j.Status != JobState.Success && j.Status != JobState.Failure)
            .OrderByDescending(j => j.StartTime)
            .Take(10)
            .ToListAsync();

        return View(activeRips);
    }

    [HttpGet("error")]
    public IActionResult Error(string message)
    {
        ViewBag.Error = message;
        return View();
    }

    [HttpGet("setup")]
    public IActionResult Setup()
    {
        return View();
    }
}
