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
    public async Task<IActionResult> Index(
        int page = 1,
        string? status = null,
        string? discType = null,
        string? search = null,
        CancellationToken ct = default)
    {
        var query = db.Jobs.AsQueryable();

        // Apply filters
        if (!string.IsNullOrWhiteSpace(status))
        {
            var pattern = $"%{status}%";
            query = query.Where(j => EF.Functions.Like(j.Status.ToString(), pattern));
        }

        if (!string.IsNullOrWhiteSpace(discType))
        {
            var pattern = $"%{discType}%";
            query = query.Where(j => EF.Functions.Like(j.DiscType.ToString(), pattern));
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{search}%";
            query = query.Where(j =>
                (j.Title != null && EF.Functions.Like(j.Title, pattern)) ||
                (j.TitleAuto != null && EF.Functions.Like(j.TitleAuto, pattern)) ||
                (j.TitleManual != null && EF.Functions.Like(j.TitleManual, pattern)));
        }

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
        ViewBag.StatusFilter = status;
        ViewBag.DiscTypeFilter = discType;
        ViewBag.SearchFilter = search;

        return View(jobs);
    }
}
