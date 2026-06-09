using ArmRipper.Core.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace ArmRipper.WebUi.Controllers;

[Authorize]
[Route("logs")]
public class LogsController(IOptions<ArmSettings> settings) : Controller
{
    private string LogPath => settings.Value.LogPath ?? "/home/arm/logs";

    [HttpGet("")]
    public IActionResult Index()
    {
        var dir = new DirectoryInfo(LogPath);
        if (!dir.Exists)
            return View(Array.Empty<LogFileEntry>());

        var files = dir.GetFiles()
            .OrderByDescending(f => f.LastWriteTime)
            .Select(f => new LogFileEntry
            {
                Name = f.Name,
                LastWriteTime = f.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"),
                SizeKb = $"{Math.Round(f.Length / 1024.0, 1):N1}"
            })
            .ToList();

        return View(files);
    }

    [HttpGet("view")]
    public IActionResult Viewer(string file, string mode = "full")
    {
        var safeFileName = Path.GetFileName(file);
        if (string.IsNullOrEmpty(safeFileName))
            return BadRequest("Invalid log file");

        var fullPath = Path.Combine(LogPath, safeFileName);
        if (!System.IO.File.Exists(fullPath))
            return NotFound("Log file not found");

        ViewBag.FileName = file;
        ViewBag.Mode = mode;
        return View();
    }

    [HttpGet("reader")]
    public IActionResult Reader(string file, string mode = "full")
    {
        var safeFileName = Path.GetFileName(file);
        if (string.IsNullOrEmpty(safeFileName))
            return BadRequest("Invalid log file");

        var fullPath = Path.Combine(LogPath, safeFileName);
        if (!System.IO.File.Exists(fullPath))
            return NotFound();

        try
        {
            using var reader = new StreamReader(fullPath);
            if (mode == "arm")
            {
                var lines = new List<string>();
                while (reader.ReadLine() is { } line)
                {
                    if (line.Contains("ARM:"))
                        lines.Add(line);
                }
                return Content(string.Join('\n', lines), "text/plain");
            }

            var content = reader.ReadToEnd();
            return Content(content, "text/plain");
        }
        catch (Exception)
        {
            return Content("Error reading log file", "text/plain");
        }
    }

    [HttpGet("download")]
    public IActionResult Download(string file)
    {
        var safeFileName = Path.GetFileName(file);
        if (string.IsNullOrEmpty(safeFileName))
            return BadRequest("Invalid log file");

        var fullPath = Path.Combine(LogPath, safeFileName);
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
