using ArmRipper.Core.Configuration;
using ArmRipper.Core.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace ArmRipper.WebUi.Controllers;

[Authorize]
[Route("logs")]
public class LogsController(ISettingsService settingsService) : Controller
{
    /// <summary>
    /// Resolves the log directory from the effective settings (the DB RipperSettings
    /// row overrides appsettings/YAML). The job logger writes job logs using the
    /// effective LogPath, so the logs page must read from that same directory.
    /// </summary>
    private async Task<string> GetLogPathAsync(CancellationToken ct = default)
    {
        var effective = await settingsService.GetEffectiveAsync(ct);
        return ArmPaths.GetLogPath(effective);
    }

    private const int PageSize = 25;

    [HttpGet("")]
    public async Task<IActionResult> Index(int page = 1, CancellationToken ct = default)
    {
        var dir = new DirectoryInfo(await GetLogPathAsync(ct));
        if (!dir.Exists)
            return View(Array.Empty<LogFileEntry>());

        var allFiles = dir.GetFiles()
            .OrderByDescending(f => f.LastWriteTime)
            .Select(f => new LogFileEntry
            {
                Name = f.Name,
                LastWriteTime = f.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"),
                SizeKb = $"{Math.Round(f.Length / 1024.0, 1):N1}"
            })
            .ToList();

        var totalFiles = allFiles.Count;
        var totalPages = Math.Max(1, (int)Math.Ceiling(totalFiles / (double)PageSize));
        page = Math.Clamp(page, 1, totalPages);

        var files = allFiles
            .Skip((page - 1) * PageSize)
            .Take(PageSize)
            .ToList();

        ViewBag.CurrentPage = page;
        ViewBag.TotalPages = totalPages;
        ViewBag.TotalFiles = totalFiles;

        return View(files);
    }

    [HttpGet("view")]
    public async Task<IActionResult> Viewer(string file, string mode = "full", CancellationToken ct = default)
    {
        var safeFileName = Path.GetFileName(file);
        if (string.IsNullOrEmpty(safeFileName))
            return BadRequest("Invalid log file");

        var fullPath = Path.Combine(await GetLogPathAsync(ct), safeFileName);
        if (!System.IO.File.Exists(fullPath))
            return NotFound("Log file not found");

        ViewBag.FileName = file;
        ViewBag.Mode = mode;
        return View();
    }

    [HttpGet("reader")]
    public async Task<IActionResult> Reader(string file, string mode = "full", CancellationToken ct = default)
    {
        var safeFileName = Path.GetFileName(file);
        if (string.IsNullOrEmpty(safeFileName))
            return BadRequest("Invalid log file");

        var fullPath = Path.Combine(await GetLogPathAsync(ct), safeFileName);
        if (!System.IO.File.Exists(fullPath))
            return NotFound();

        try
        {
            using var reader = new StreamReader(fullPath);
            var content = reader.ReadToEnd();
            return Content(content, "text/plain");
        }
        catch (Exception)
        {
            return Content("Error reading log file", "text/plain");
        }
    }

    [HttpGet("download")]
    public async Task<IActionResult> Download(string file, CancellationToken ct = default)
    {
        var safeFileName = Path.GetFileName(file);
        if (string.IsNullOrEmpty(safeFileName))
            return BadRequest("Invalid log file");

        var fullPath = Path.Combine(await GetLogPathAsync(ct), safeFileName);
        if (!System.IO.File.Exists(fullPath))
            return NotFound();

        return PhysicalFile(fullPath, "text/plain", file);
    }
}

public class LogFileEntry
{
    public string Name { get; set; } = "";
    public string LastWriteTime { get; set; } = "";
    public string SizeKb { get; set; } = "";
}
