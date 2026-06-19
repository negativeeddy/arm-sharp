using ArmRipper.Core.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ArmRipper.WebUi.Controllers;

[Authorize]
[Route("history")]
public class HistoryController(ArmDbContext db) : Controller
{
    private const int PageSize = 25;

    [HttpGet("")]
    public async Task<IActionResult> Index(int page = 1, CancellationToken ct = default)
    {
        var query = db.Jobs.AsQueryable();

        var totalJobs = await query.CountAsync(ct);
        var totalPages = Math.Max(1, (int)Math.Ceiling(totalJobs / (double)PageSize));
        page = Math.Clamp(page, 1, totalPages);

        var jobs = await query
            .OrderByDescending(j => j.Id)
            .Skip((page - 1) * PageSize)
            .Take(PageSize)
            .ToListAsync(ct);

        ViewBag.CurrentPage = page;
        ViewBag.TotalPages = totalPages;
        ViewBag.TotalJobs = totalJobs;

        return View(jobs);
    }
}
