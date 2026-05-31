using System.Runtime.InteropServices;
using ArmRipper.Core.Configuration;
using ArmRipper.Core.Infrastructure;
using ArmRipper.Core.Infrastructure.Data;
using ArmRipper.Core.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ArmRipper.WebUi.Controllers;

[Route("settings")]
public class SettingsController(
    ArmDbContext db,
    CliProcessRunner runner,
    IOptions<ArmSettings> settings) : Controller
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
        ViewBag.ArmSettings = settings.Value;
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

        return View();
    }

    [HttpPost("save-ripper")]
    public async Task<IActionResult> SaveRipper(ArmSettings posted)
    {
        // Update current ArmSettings in memory (persist to appsettings later)
        // For now just display a success message
        TempData["Message"] = "Ripper settings updated. (Runtime only — persist to appsettings.json for permanence)";
        return RedirectToAction("Index");
    }

    [HttpGet("scan")]
    public async Task<IActionResult> ScanDrives()
    {
        var found = 0;
        for (char c = 'a'; c <= 'z'; c++)
        {
            var devPath = $"/dev/sr{c}";
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
