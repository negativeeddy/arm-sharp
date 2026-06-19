using ArmRipper.Core.Infrastructure;
using ArmRipper.Core.Infrastructure.Data;
using ArmRipper.Core.Models;
using ArmRipper.WebUi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ArmRipper.WebUi.Controllers;

[Authorize]
[Route("")]
public class HomeController(ArmDbContext db, ICliProcessRunner runner, IHardwareEncoderInfoService hardwareEncoderInfoService) : Controller
{
    [HttpGet("")]
    [HttpGet("index")]
    public async Task<IActionResult> Index()
    {
        var activeRips = await db.Jobs
            .Include(j => j.Config)
            .Where(j => j.Status != JobState.Success && j.Status != JobState.Failure && j.Status != JobState.Cancelled)
            .OrderByDescending(j => j.StartTime)
            .Take(10)
            .ToListAsync();

        // Pass drives and active job device paths for the Drives widget
        ViewBag.Drives = await db.SystemDrives.ToListAsync();
        ViewBag.ActiveJobDevPaths = activeRips
            .Where(j => !string.IsNullOrEmpty(j.DevPath))
            .Select(j => j.DevPath!)
            .ToHashSet();

        ViewBag.Hostname = Environment.MachineName;
        ViewBag.CpuCount = Environment.ProcessorCount;

        try
        {
            ViewBag.CpuModel = (await System.IO.File.ReadAllTextAsync("/proc/cpuinfo"))
                .Split('\n')
                .FirstOrDefault(l => l.StartsWith("model name"))
                ?.Split(':')[1]
                ?.Trim();
        }
        catch { }

        try
        {
            var tempPath = Directory.GetFiles("/sys/class/thermal", "thermal_zone*")
                .Select(d => Path.Combine(d, "temp"))
                .FirstOrDefault(f => System.IO.File.Exists(f)
                    && System.IO.File.ReadAllText(Path.Combine(Path.GetDirectoryName(f)!, "type")).Trim() == "x86_pkg_temp");
            if (tempPath is not null)
            {
                var raw = await System.IO.File.ReadAllTextAsync(tempPath);
                if (int.TryParse(raw.Trim(), out var millideg))
                    ViewBag.CpuTemp = millideg / 1000.0;
            }
        }
        catch { }

        try
        {
            var topResult = await runner.RunAsync("top", "-bn1", timeoutMs: 5000);
            if (topResult.ExitCode == 0)
            {
                var cpuLine = topResult.StdOut.Split('\n')
                    .FirstOrDefault(l => l.StartsWith("%Cpu"));
                if (cpuLine is not null)
                {
                    var idleMatch = System.Text.RegularExpressions.Regex.Match(cpuLine, @"([\d.]+)\s*id");
                    if (idleMatch.Success && float.TryParse(idleMatch.Groups[1].Value, out var idle))
                        ViewBag.CpuUsage = Math.Round(100.0 - idle, 1);
                }
            }
        }
        catch { }

        try
        {
            var memInfo = await System.IO.File.ReadAllTextAsync("/proc/meminfo");
            long ParseMem(string prefix) =>
                memInfo.Split('\n')
                    .FirstOrDefault(l => l.StartsWith(prefix))
                    ?.Split(':')[1]
                    ?.Trim()
                    ?.Split(' ')[0] is string val && long.TryParse(val, out var n)
                    ? n : 0;

            var totalKb = ParseMem("MemTotal");
            var freeKb = ParseMem("MemFree");
            var availKb = ParseMem("MemAvailable");
            ViewBag.MemTotal = totalKb * 1024;
            ViewBag.MemFree = freeKb * 1024;
            ViewBag.MemUsed = (totalKb - availKb) * 1024;
        }
        catch { }

        try
        {
            ViewBag.StorageTranscode = GetDriveInfo("/home/arm/media/transcode");
            ViewBag.StorageCompleted = GetDriveInfo("/home/arm/media/completed");
        }
        catch { }

        ViewBag.HardwareEncoders = await hardwareEncoderInfoService.GetHardwareEncoderInfoAsync();

        return View(activeRips);
    }

    private static Dictionary<string, object>? GetDriveInfo(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            // DriveInfo(root) always reports the root filesystem, not the actual mount.
            // Use statvfs via df to get stats for the real mount point of this path.
            var psi = new System.Diagnostics.ProcessStartInfo("df", $"--block-size=1 --output=size,avail \"{path}\"")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            var output = proc?.StandardOutput.ReadToEnd() ?? "";
            proc?.WaitForExit();
            // Output: header line + data line with size and avail in bytes
            var lines = output.Trim().Split('\n');
            if (lines.Length < 2) return null;
            var parts = lines[1].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !long.TryParse(parts[0], out var total) || !long.TryParse(parts[1], out var free))
                return null;
            var used = total - free;
            return new Dictionary<string, object>
            {
                ["path"] = path,
                ["total"] = total,
                ["free"] = free,
                ["used"] = used,
                ["used_pct"] = total > 0 ? Math.Round((double)used / total * 100, 1) : 0
            };
        }
        catch { return null; }
    }

    [AllowAnonymous]
    [AcceptVerbs("GET", "POST"), Route("error")]
    public IActionResult Error(string message, [FromQuery] int? code = null)
    {
        if (code.HasValue)
            Response.StatusCode = code.Value;
        ViewBag.Error = message;
        return View();
    }

    [AllowAnonymous]
    [HttpGet("setup")]
    public IActionResult Setup()
    {
        return View();
    }
}
