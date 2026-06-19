using ArmRipper.Core.Infrastructure.Data;
using ArmRipper.Core.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ArmRipper.WebUi.Controllers;

[Authorize]
[Route("database")]
public class DatabaseController(ArmDbContext db) : Controller
{
    private const int PageSize = 20;

    [HttpGet("")]
    public async Task<IActionResult> Index(int page = 1, string? filter = null, string? search = null)
    {
        var query = db.Jobs
            .Include(j => j.Config)
            .AsQueryable();

        if (!string.IsNullOrEmpty(filter))
        {
            query = filter.ToLower() switch
            {
                "success" => query.Where(j => j.Status == JobState.Success),
                "failed" => query.Where(j => j.Status == JobState.Failure),
                "active" => query.Where(j => j.Status == JobState.Active),
                _ => query
            };
        }

        if (!string.IsNullOrEmpty(search))
        {
            query = query.Where(j =>
                (j.Title != null && j.Title.Contains(search)) ||
                (j.TitleAuto != null && j.TitleAuto.Contains(search)) ||
                (j.Label != null && j.Label.Contains(search)));
        }

        var totalJobs = await query.CountAsync();
        var totalPages = Math.Max(1, (int)Math.Ceiling(totalJobs / (double)PageSize));
        page = Math.Clamp(page, 1, totalPages);

        var jobs = await query
            .OrderByDescending(j => j.Id)
            .Skip((page - 1) * PageSize)
            .Take(PageSize)
            .ToListAsync();

        ViewBag.CurrentPage = page;
        ViewBag.TotalPages = totalPages;
        ViewBag.TotalJobs = totalJobs;
        ViewBag.Filter = filter;
        ViewBag.Search = search;

        return View(jobs);
    }

    [HttpGet("update")]
    public IActionResult Update()
    {
        return View();
    }

    [HttpPost("update")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Update(string action)
    {
        if (action == "migrate")
        {
            await db.Database.MigrateAsync();
            TempData["Message"] = "Database migration successful!";
        }
        else if (action == "create")
        {
            await db.Database.EnsureCreatedAsync();
            TempData["Message"] = "Database created successfully!";
        }

        return RedirectToAction("Index");
    }

    [HttpGet("import")]
    public async Task<IActionResult> Import(CancellationToken ct = default)
    {
        var completedPath = "/home/arm/media";
        if (!Directory.Exists(completedPath))
            return Json(new { message = "Completed path does not exist", path = completedPath });

        var dirs = Directory.GetDirectories(completedPath);
        var added = 0;
        var errors = new List<string>();

        foreach (var dir in dirs)
        {
            var dirName = Path.GetFileName(dir);
            var match = System.Text.RegularExpressions.Regex.Match(dirName, @"^(.+?) \((\d{4})\)$");
            if (!match.Success)
            {
                errors.Add(dirName);
                continue;
            }

            var title = match.Groups[1].Value.Trim();
            var year = match.Groups[2].Value;

            var exists = await db.Jobs.AnyAsync(j => j.Title == title && j.Year == year);
            if (exists)
                continue;

            db.Jobs.Add(new Job
            {
                Title = title,
                Year = year,
                TitleAuto = title,
                YearAuto = year,
                Status = JobState.Success,
                VideoType = "movie",
                HasNiceTitle = true,
                Path = dir,
                StartTime = Directory.GetCreationTimeUtc(dir)
            });
            added++;
        }

        await db.SaveChangesAsync(ct);
        return Json(new { added, notFound = errors });
    }

    [HttpPost("delete/{id}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct = default)
    {
        var job = await db.Jobs.FindAsync(id);
        if (job is null)
            return NotFound();

        db.Jobs.Remove(job);
        await db.SaveChangesAsync(ct);
        TempData["Message"] = $"Job {id} deleted.";
        return RedirectToAction("Index");
    }
}
