using System.Runtime.InteropServices;
using ArmRipper.Core.Infrastructure;
using ArmRipper.Core.Infrastructure.Data;
using ArmRipper.Core.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ArmRipper.WebUi.Controllers;

[Authorize]
[Route("")]
public class HomeController(ArmDbContext db, ICliProcessRunner runner) : Controller
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

        ViewBag.HardwareEncoders = await GetHardwareEncoderInfoAsync();

        return View(activeRips);
    }

    private static Dictionary<string, object>? GetDriveInfo(string path)
    {
        try
        {
            var dir = Directory.CreateDirectory(path);
            var drive = new DriveInfo(dir.Root.FullName);
            if (!drive.IsReady) return null;
            var total = drive.TotalSize;
            var free = drive.AvailableFreeSpace;
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

    private async Task<IReadOnlyList<Dictionary<string, object>>> GetHardwareEncoderInfoAsync()
    {
        var encoders = new List<Dictionary<string, object>>();
        await AddNvidiaEncoderAsync(encoders);
        await AddFfmpegEncodersAsync(encoders);
        if (encoders.Count == 0)
            encoders.Add(new Dictionary<string, object> { ["available"] = false });
        return encoders;
    }

    private async Task AddNvidiaEncoderAsync(List<Dictionary<string, object>> encoders)
    {
        try
        {
            var gpuResult = await runner.RunAsync("nvidia-smi",
                "--query-gpu=index,name --format=csv,noheader",
                timeoutMs: 5000);

            if (gpuResult.ExitCode != 0 || string.IsNullOrWhiteSpace(gpuResult.StdOut))
                return;

            foreach (var line in gpuResult.StdOut.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split(',');
                var nv = new Dictionary<string, object>
                {
                    ["vendor"] = "NVIDIA",
                    ["type"] = "NVENC",
                    ["available"] = true
                };
                if (parts.Length > 0) nv["index"] = parts[0].Trim();
                if (parts.Length > 1) nv["gpu"] = parts[1].Trim();
                encoders.Add(nv);
            }
        }
        catch { }
    }

    private async Task AddFfmpegEncodersAsync(List<Dictionary<string, object>> encoders)
    {
        try
        {
            var result = await runner.RunAsync("ffmpeg", "-hide_banner -encoders", timeoutMs: 10_000);
            if (result.ExitCode != 0) return;

            var seen = new HashSet<string>();
            foreach (var line in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith("V")) continue;

                var match = System.Text.RegularExpressions.Regex.Match(trimmed, @"\s(\w+)$");
                if (!match.Success) continue;

                var encoderName = match.Groups[1].Value;

                if (encoderName.EndsWith("_nvenc") || encoderName == "nvenc" || encoderName.EndsWith("_nvenc_hevc"))
                { if (seen.Add("nvidia")) AddEncoder(encoders, "NVIDIA", "NVENC"); }
                else if (encoderName.EndsWith("_qsv"))
                { if (seen.Add("intel")) AddEncoder(encoders, "Intel", "QuickSync"); }
                else if (encoderName.EndsWith("_amf"))
                { if (seen.Add("amd")) AddEncoder(encoders, "AMD", "AMF"); }
                else if (encoderName.EndsWith("_vaapi"))
                { if (seen.Add("vaapi")) AddEncoder(encoders, "VA-API", "VA-API"); }
            }
        }
        catch { }
    }

    private static void AddEncoder(List<Dictionary<string, object>> encoders, string vendor, string type)
    {
        encoders.Add(new Dictionary<string, object>
        {
            ["vendor"] = vendor,
            ["type"] = type,
            ["available"] = true
        });
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
