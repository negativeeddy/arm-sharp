using System.Runtime.InteropServices;
using ArmRipper.Core.Configuration;
using ArmRipper.Core.Infrastructure;
using ArmRipper.Core.Infrastructure.Data;
using ArmRipper.Core.Models;
using ArmRipper.Core.Rip;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ArmRipper.WebUi.Controllers;

[Authorize]
[Route("settings")]
public class SettingsController(
    ArmDbContext db,
    ICliProcessRunner runner,
    IOptions<ArmSettings> settings,
    IBackgroundRipService backgroundRip) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        var drives = await db.SystemDrives.ToListAsync();
        var systemInfo = await db.SystemInfos.FirstOrDefaultAsync();
        var uiCfg = await db.UiSettings.FirstOrDefaultAsync();

        ViewBag.Drives = drives;
        ViewBag.SystemInfo = systemInfo;
        ViewBag.UiSettings = uiCfg;

        // Merge DB-stored ripper settings on top of defaults
        var mergedSettings = new ArmSettings();
        foreach (var prop in typeof(ArmSettings).GetProperties())
        {
            if (prop.CanWrite)
            {
                var defaultValue = prop.GetValue(settings.Value);
                prop.SetValue(mergedSettings, defaultValue);
            }
        }
        var savedRipper = await db.RipperSettings.FirstOrDefaultAsync();
        if (savedRipper is not null)
        {
            var saved = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(savedRipper.SettingsJson);
            if (saved is not null)
            {
                foreach (var (key, value) in saved)
                {
                    var prop = typeof(ArmSettings).GetProperty(key);
                    if (prop is not null && prop.CanWrite)
                    {
                        var converted = System.Text.Json.JsonSerializer.Deserialize(value.GetRawText(), prop.PropertyType);
                        if (converted is not null)
                            prop.SetValue(mergedSettings, converted);
                    }
                }
            }
        }
        ViewBag.ArmSettings = mergedSettings;
        ViewBag.Hostname = Environment.MachineName;
        ViewBag.OsDesc = RuntimeInformation.OSDescription;
        ViewBag.ProcCount = Environment.ProcessorCount;

        var totalJobs = await db.Jobs.CountAsync();
        var failedJobs = await db.Jobs.CountAsync(j => j.Status == JobState.Failure);
        var movies = await db.Jobs.CountAsync(j => j.VideoType == "movie");
        var series = await db.Jobs.CountAsync(j => j.VideoType == "series");

        ViewBag.Stats = new Dictionary<string, object>
        {
            ["total_rips"] = totalJobs,
            ["failed_rips"] = failedJobs,
            ["movies"] = movies,
            ["series"] = series
        };

        ViewBag.HardwareEncoders = await GetHardwareEncoderInfoAsync();

        // Read abcde config if available
        var abcdePath = "/etc/arm/config/abcde.conf";
        var abcdeConfig = new Dictionary<string, string>();
        if (System.IO.File.Exists(abcdePath))
        {
            foreach (var line in await System.IO.File.ReadAllLinesAsync(abcdePath))
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#')) continue;
                var eq = trimmed.IndexOf('=');
                if (eq > 0)
                    abcdeConfig[trimmed[..eq].Trim()] = trimmed[(eq + 1)..].Trim();
            }
        }
        ViewBag.AbcdeConfig = abcdeConfig;

        return View();
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
                "--query-gpu=index,name,driver_version,compute_cap,utilization.encoder --format=csv,noheader",
                timeoutMs: 5000);

            if (gpuResult.ExitCode != 0 || string.IsNullOrWhiteSpace(gpuResult.StdOut))
                return;

            var gpuLines = gpuResult.StdOut.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            foreach (var gpuLine in gpuLines)
            {
                var nv = new Dictionary<string, object>
                {
                    ["vendor"] = "NVIDIA",
                    ["type"] = "NVENC",
                    ["available"] = true
                };
                var parts = gpuLine.Split(',');
                if (parts.Length > 0) nv["index"] = parts[0].Trim();
                if (parts.Length > 1) nv["gpu"] = parts[1].Trim();
                if (parts.Length > 2) nv["driver"] = parts[2].Trim();
                if (parts.Length > 3) nv["compute_cap"] = parts[3].Trim();
                if (parts.Length > 4) nv["encoder_util"] = parts[4].Trim();

                // Per-GPU detail
                var detailResult = await runner.RunAsync("nvidia-smi",
                    $"-q -i {nv["index"]}", timeoutMs: 5000);
                if (detailResult.ExitCode == 0)
                {
                    var inEncoderSection = false;
                    foreach (var line in detailResult.StdOut.Split('\n'))
                    {
                        if (line.Trim() == "Encoder Stats")
                        {
                            inEncoderSection = true;
                            continue;
                        }
                        if (inEncoderSection)
                        {
                            if (string.IsNullOrWhiteSpace(line) || !line.Contains(':'))
                            {
                                inEncoderSection = false;
                                continue;
                            }
                            var colonIdx = line.IndexOf(':');
                            var key = line[..colonIdx].Trim();
                            var val = line[(colonIdx + 1)..].Trim();
                            switch (key)
                            {
                                case "Active Sessions": nv["sessions"] = val; break;
                                case "Average FPS": nv["avg_fps"] = val; break;
                                case "Average Latency": nv["avg_latency"] = val; break;
                            }
                        }
                    }
                }

                if (nv.TryGetValue("compute_cap", out var cc) && cc is string cs && cs.StartsWith("6."))
                    nv["note"] = "Pascal GPU — HEVC B-frames not supported (bf=0 required)";

                encoders.Add(nv);
            }
        }
        catch { /* nvidia-smi not available */ }
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
                if (!trimmed.StartsWith("V")) continue; // only video encoders

                var match = System.Text.RegularExpressions.Regex.Match(trimmed, @"\s(\w+)$");
                if (!match.Success) continue;

                var encoderName = match.Groups[1].Value;
                var codec = encoderName.Split('_')[0];

                if (encoderName.EndsWith("_nvenc") || encoderName == "nvenc" || encoderName.EndsWith("_nvenc_hevc"))
                {
                    if (!seen.Add("nvidia")) continue;
                    AddOrUpdateFfmpegEncoder(encoders, "NVIDIA", "NVENC", codec);
                }
                else if (encoderName.EndsWith("_qsv"))
                {
                    if (!seen.Add("intel")) continue;
                    AddOrUpdateFfmpegEncoder(encoders, "Intel", "QuickSync", codec);
                }
                else if (encoderName.EndsWith("_amf"))
                {
                    if (!seen.Add("amd")) continue;
                    AddOrUpdateFfmpegEncoder(encoders, "AMD", "AMF", codec);
                }
                else if (encoderName.EndsWith("_vaapi"))
                {
                    if (!seen.Add("vaapi")) continue;
                    AddOrUpdateFfmpegEncoder(encoders, "VA-API", "VA-API", codec);
                }
            }
        }
        catch { /* ffmpeg not available */ }
    }

    private static void AddOrUpdateFfmpegEncoder(
        List<Dictionary<string, object>> encoders, string vendor, string type, string codec)
    {
        var existing = encoders.FirstOrDefault(e =>
            e.TryGetValue("type", out var t) && t is string ts && ts == type);
        if (existing is null)
        {
            existing = new Dictionary<string, object>
            {
                ["vendor"] = vendor,
                ["type"] = type,
                ["available"] = true,
                ["codecs"] = new List<string>()
            };
            encoders.Add(existing);
        }
        if (existing["codecs"] is List<string> codecs && !codecs.Contains(codec))
            codecs.Add(codec);
    }

    [HttpPost("save-ripper")]
    public async Task<IActionResult> SaveRipper(ArmSettings posted)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(posted);
        var existing = await db.RipperSettings.FirstOrDefaultAsync();
        if (existing is null)
        {
            db.RipperSettings.Add(new RipperSettings { SettingsJson = json });
        }
        else
        {
            existing.SettingsJson = json;
        }
        await db.SaveChangesAsync();

        TempData["Message"] = "Ripper settings saved to database.";
        return RedirectToAction("Index");
    }

    [HttpGet("scan")]
    public async Task<IActionResult> ScanDrives()
    {
        var found = 0;
        for (int i = 0; i <= 25; i++)
        {
            var devPath = $"/dev/sr{i}";
            if (!System.IO.File.Exists(devPath))
                continue;

            var exists = await db.SystemDrives.AnyAsync(d => d.Mount == devPath);
            if (exists)
                continue;

            var result = await runner.RunAsync("udevadm", $"info --query=property {devPath}", timeoutMs: 10_000);
            var model = "Unknown";
            var vendor = "";
            var serial = "";
            var firmware = "";
            var hasCd = false;
            var hasDvd = false;
            var hasBd = false;
            foreach (var line in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.StartsWith("ID_MODEL="))
                    model = line["ID_MODEL=".Length..].Trim('"');
                else if (line.StartsWith("ID_VENDOR="))
                    vendor = line["ID_VENDOR=".Length..].Trim('"');
                else if (line.StartsWith("ID_SERIAL="))
                    serial = line["ID_SERIAL=".Length..].Trim('"');
                else if (line.StartsWith("ID_REVISION="))
                    firmware = line["ID_REVISION=".Length..].Trim('"');
                else if (line.StartsWith("ID_CDROM=") && line.EndsWith("1"))
                    hasCd = true;
                else if (line.StartsWith("ID_CDROM_DVD=") && line.EndsWith("1"))
                    hasDvd = true;
                else if (line.StartsWith("ID_CDROM_BD=") && line.EndsWith("1"))
                    hasBd = true;
            }

            db.SystemDrives.Add(new SystemDrive
            {
                Mount = devPath,
                Model = $"{vendor} {model}".Trim(),
                Serial = serial,
                Firmware = firmware,
                DriveMode = "autodetect",
                ReadCd = hasCd,
                ReadDvd = hasDvd,
                ReadBd = hasBd
            });
            found++;
        }

        await db.SaveChangesAsync();
        TempData["Message"] = $"Found {found} new drive(s).";
        return RedirectToAction("Index");
    }

    private static readonly string[] DriveModes = ["autodetect", "manual", "disabled"];

    [HttpPost("drive-toggle-mode/{id:int}")]
    public async Task<IActionResult> ToggleDriveMode(int id)
    {
        var drive = await db.SystemDrives.FindAsync(id);
        if (drive is null)
            return NotFound();

        var idx = Array.IndexOf(DriveModes, drive.DriveMode);
        drive.DriveMode = DriveModes[(idx + 1) % DriveModes.Length];
        await db.SaveChangesAsync();
        TempData["Message"] = $"Drive {drive.Mount} mode → {drive.DriveMode}";
        return RedirectToAction("Index");
    }

    [HttpPost("drive-remove/{id:int}")]
    public async Task<IActionResult> RemoveDrive(int id)
    {
        var drive = await db.SystemDrives.FindAsync(id);
        if (drive is null)
            return NotFound();

        db.SystemDrives.Remove(drive);
        await db.SaveChangesAsync();
        TempData["Message"] = $"Removed drive {drive.Mount}";
        return RedirectToAction("Index");
    }

    [HttpPost("start-rip")]
    public async Task<IActionResult> StartRip(string devPath)
    {
        backgroundRip.StartRip(devPath);

        // Wait briefly for the background task to create the job in DB
        await Task.Delay(500);

        // Find the most recent job for this device
        var job = await db.Jobs
            .Where(j => j.DevPath == devPath)
            .OrderByDescending(j => j.StartTime)
            .FirstOrDefaultAsync();

        if (job is not null)
            return RedirectToAction("JobDetail", "Jobs", new { jobId = job.Id });

        TempData["Message"] = $"Rip started for {devPath}. Job page will appear shortly.";
        return RedirectToAction("Index");
    }

    [HttpPost("save-ui")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveUi(string theme, int refreshRate, string iconStyle)
    {
        var ui = await db.UiSettings.FirstOrDefaultAsync();
        if (ui is null)
        {
            ui = new UiSettings { Theme = theme, RefreshRate = refreshRate, IconStyle = iconStyle };
            db.UiSettings.Add(ui);
        }
        else
        {
            ui.Theme = theme;
            ui.RefreshRate = refreshRate;
            ui.IconStyle = iconStyle;
        }
        await db.SaveChangesAsync();
        TempData["Message"] = "UI settings saved.";
        return RedirectToAction("Index");
    }

    [HttpPost("save-abcde")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveAbcde(string outputFormat, string outputDir)
    {
        try
        {
            var abcdeConf = "/etc/arm/config/abcde.conf";
            var lines = new List<string>
            {
                $"OUTPUTTYPE={outputFormat}",
                $"OUTPUTDIR={outputDir}",
                "CDROMREADERSYNTAX=cdparanoia",
                "WGET=wget",
                "MUSICBRAINZ=musicbrainz"
            };
            await System.IO.File.WriteAllLinesAsync(abcdeConf, lines);
            TempData["Message"] = $"abcde config saved to {abcdeConf}.";
        }
        catch (Exception ex)
        {
            TempData["Message"] = $"Failed to save abcde config: {ex.Message}";
        }
        return RedirectToAction("Index");
    }

    [HttpPost("eject")]
    public async Task<IActionResult> EjectDrive(string devPath)
    {
        try
        {
            await runner.RunAsync("umount", devPath, timeoutMs: 10_000);
            await runner.RunAsync("eject", devPath, timeoutMs: 10_000);
            TempData["Message"] = $"Ejected {devPath}";
        }
        catch (Exception ex)
        {
            TempData["Message"] = $"Failed to eject {devPath}: {ex.Message}";
        }
        return RedirectToAction("Index");
    }

    [HttpGet("sysinfo")]
    public async Task<IActionResult> RefreshSysInfo()
    {
        var existing = await db.SystemInfos.FirstOrDefaultAsync();
        if (existing is null)
        {
            existing = new SystemInfo
            {
                Hostname = Environment.MachineName,
                CpuInfo = $"{Environment.ProcessorCount} cores",
                RamInfo = $"{GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024 * 1024)} GB",
                OsInfo = RuntimeInformation.OSDescription,
                ArmVersion = "1.0.0"
            };
            db.SystemInfos.Add(existing);
        }
        else
        {
            existing.Hostname = Environment.MachineName;
            existing.CpuInfo = $"{Environment.ProcessorCount} cores";
            existing.RamInfo = $"{GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024 * 1024)} GB";
            existing.OsInfo = RuntimeInformation.OSDescription;
        }
        await db.SaveChangesAsync();
        TempData["Message"] = "System info updated.";
        return RedirectToAction("Index");
    }
}
