using System.Runtime.InteropServices;
using ArmRipper.Core.Configuration;
using ArmRipper.Core.Infrastructure;
using ArmRipper.Core.Infrastructure.Data;
using ArmRipper.Core.Models;
using ArmRipper.WebUi.Services;
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
    IHardwareEncoderInfoService hardwareEncoderInfoService,
    IOptions<ArmSettings> settings,
    IBackgroundRipService backgroundRip) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct = default)
    {
        var drives = await db.SystemDrives.ToListAsync(ct);
        var systemInfo = await db.SystemInfos.FirstOrDefaultAsync(ct);
        var uiCfg = await db.UiSettings.FirstOrDefaultAsync(ct);

        ViewBag.Drives = drives;
        ViewBag.ActiveJobDevPaths = (await db.Jobs
            .Where(j => j.Status != JobState.Success && j.Status != JobState.Failure && j.Status != JobState.Cancelled && !string.IsNullOrEmpty(j.DevPath))
            .Select(j => j.DevPath!)
            .ToListAsync(ct))
            .ToHashSet();
        ViewBag.SystemInfo = systemInfo;
        ViewBag.UiSettings = uiCfg;

        // Merge DB-stored ripper settings on top of defaults
        var mergedSettings = await SettingsHelper.GetEffectiveSettingsAsync(db, settings.Value, ct);
        ViewBag.ArmSettings = mergedSettings;
        ViewBag.Hostname = Environment.MachineName;
        ViewBag.OsDesc = RuntimeInformation.OSDescription;
        ViewBag.ProcCount = Environment.ProcessorCount;

        // Tab persistence: stay on the tab that was active after a save
        ViewBag.ActiveTab = TempData["ActiveTab"] as string ?? "tab3";
        TempData.Keep("ActiveTab");

        var totalJobs = await db.Jobs.CountAsync(ct);
        var failedJobs = await db.Jobs.CountAsync(j => j.Status == JobState.Failure, ct);
        var movies = await db.Jobs.CountAsync(j => j.VideoType == "movie", ct);
        var series = await db.Jobs.CountAsync(j => j.VideoType == "series", ct);

        ViewBag.Stats = new Dictionary<string, object>
        {
            ["total_rips"] = totalJobs,
            ["failed_rips"] = failedJobs,
            ["movies"] = movies,
            ["series"] = series
        };

        ViewBag.HardwareEncoders = await hardwareEncoderInfoService.GetHardwareEncoderInfoAsync(includeDetailedNvidiaStats: true);

        // Count pending CRC64 submissions for the Online DB tab (excludes already-submitted)
        var pendingCrcCount = await db.Jobs
            .Where(j => j.DiscType == DiscType.Dvd &&
                        !string.IsNullOrEmpty(j.CrcId) &&
                        (j.HasNiceTitle || !string.IsNullOrEmpty(j.TitleManual)) &&
                        !EF.Functions.Like(j.CompletedStages ?? "", "%CrcSubmitted%"))
            .CountAsync(ct);
        ViewBag.PendingCrcCount = pendingCrcCount;

        // Read abcde config if available
        var abcdePath = "/etc/arm/config/abcde.conf";
        var abcdeConfig = new Dictionary<string, string>();
        if (System.IO.File.Exists(abcdePath))
        {
            foreach (var line in await System.IO.File.ReadAllLinesAsync(abcdePath, ct))
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

    [HttpPost("save-ripper")]
    public async Task<IActionResult> SaveRipper(
        string? RipMethod, string? MkvArgs, int? MinLength, int? MaxLength,
        int? MaxConcurrentRips,
        CancellationToken ct = default)
    {
        // Read checkboxes from raw form values — the hidden-false trick sends
        // multiple values for the same key; bool.TryParse on the first value
        // would incorrectly return false. Instead check if "true" is present.
        bool MainFeature = Request.Form["MainFeature"].Contains("true");
        bool AutoEject = Request.Form["AutoEject"].Contains("true");

        var fields = new Dictionary<string, string?>
        {
            ["RipMethod"] = RipMethod is not null ? JsonSerialize(RipMethod) : null,
            ["MkvArgs"] = MkvArgs is not null ? JsonSerialize(MkvArgs) : null,
            ["MinLength"] = JsonSerialize(MinLength ?? 600),
            ["MaxLength"] = JsonSerialize(MaxLength ?? 99999),
            ["MaxConcurrentRips"] = JsonSerialize(MaxConcurrentRips ?? 1),
            ["MainFeature"] = JsonSerialize(MainFeature),
            ["AutoEject"] = JsonSerialize(AutoEject),
        };

        await SettingsHelper.MergeIntoDbAsync(db, fields, ct);
        TempData["Message"] = "Ripper settings saved.";
        TempData["ActiveTab"] = "tab3";
        return RedirectToAction("Index");
    }

    [HttpPost("save-transcode")]
    public async Task<IActionResult> SaveTranscode(
        string? DestExt, int? MaxConcurrentTranscodes,
        string? HbPresetDvd, string? HbPresetBd,
        string? HbArgsDvd, string? HbArgsBd,
        string? FfmpegCli, string? FfmpegPreFileArgs, string? FfmpegPostFileArgs,
        CancellationToken ct = default)
    {
        // Read checkboxes from raw form values (same hidden-false trick issue)
        bool SkipTranscode = Request.Form["SkipTranscode"].Contains("true");
        bool UseFfmpeg = Request.Form["UseFfmpeg"].Contains("true");
        bool DelRawFiles = Request.Form["DelRawFiles"].Contains("true");

        var fields = new Dictionary<string, string?>
        {
            ["SkipTranscode"] = JsonSerialize(SkipTranscode),
            ["UseFfmpeg"] = JsonSerialize(UseFfmpeg),
            ["DelRawFiles"] = JsonSerialize(DelRawFiles),
            ["DestExt"] = DestExt is not null ? JsonSerialize(DestExt) : null,
            ["MaxConcurrentTranscodes"] = JsonSerialize(MaxConcurrentTranscodes),
            ["HbPresetDvd"] = HbPresetDvd is not null ? JsonSerialize(HbPresetDvd) : null,
            ["HbPresetBd"] = HbPresetBd is not null ? JsonSerialize(HbPresetBd) : null,
            ["HbArgsDvd"] = HbArgsDvd is not null ? JsonSerialize(HbArgsDvd) : null,
            ["HbArgsBd"] = HbArgsBd is not null ? JsonSerialize(HbArgsBd) : null,
            ["FfmpegCli"] = FfmpegCli is not null ? JsonSerialize(FfmpegCli) : null,
            ["FfmpegPreFileArgs"] = FfmpegPreFileArgs is not null ? JsonSerialize(FfmpegPreFileArgs) : null,
            ["FfmpegPostFileArgs"] = FfmpegPostFileArgs is not null ? JsonSerialize(FfmpegPostFileArgs) : null,
        };

        await SettingsHelper.MergeIntoDbAsync(db, fields, ct);
        TempData["Message"] = "Transcoding settings saved.";
        TempData["ActiveTab"] = "tab8";
        return RedirectToAction("Index");
    }

    private static string JsonSerialize<T>(T value) =>
        System.Text.Json.JsonSerializer.Serialize(value);

    [HttpPost("reset-settings")]
    public async Task<IActionResult> ResetSettings(CancellationToken ct = default)
    {
        await SettingsHelper.SeedFromFileAsync(db, settings.Value, force: true, ct);
        TempData["Message"] = "Settings reset to file defaults.";
        return RedirectToAction("Index");
    }

    [HttpGet("scan")]
    public async Task<IActionResult> ScanDrives(CancellationToken ct = default)
    {
        var found = 0;
        for (int i = 0; i <= 25; i++)
        {
            var devPath = $"/dev/sr{i}";
            if (!System.IO.File.Exists(devPath))
                continue;

            var exists = await db.SystemDrives.AnyAsync(d => d.Mount == devPath, ct);
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

        await db.SaveChangesAsync(ct);
        TempData["Message"] = $"Found {found} new drive(s).";
        return RedirectToAction("Index");
    }

    private static readonly string[] DriveModes = ["autodetect", "manual", "disabled"];

    [HttpPost("drive-toggle-mode/{id:int}")]
    public async Task<IActionResult> ToggleDriveMode(int id, CancellationToken ct = default)
    {
        var drive = await db.SystemDrives.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (drive is null)
            return NotFound();

        var idx = Array.IndexOf(DriveModes, drive.DriveMode);
        drive.DriveMode = DriveModes[(idx + 1) % DriveModes.Length];
        await db.SaveChangesAsync(ct);
        TempData["Message"] = $"Drive {drive.Mount} mode → {drive.DriveMode}";
        return RedirectToAction("Index");
    }

    [HttpPost("drive-remove/{id:int}")]
    public async Task<IActionResult> RemoveDrive(int id, CancellationToken ct = default)
    {
        var drive = await db.SystemDrives.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (drive is null)
            return NotFound();

        db.SystemDrives.Remove(drive);
        await db.SaveChangesAsync(ct);
        TempData["Message"] = $"Removed drive {drive.Mount}";
        return RedirectToAction("Index");
    }

    [HttpPost("start-rip")]
    public async Task<IActionResult> StartRip(string devPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(devPath) || !devPath.StartsWith("/dev/sr", StringComparison.Ordinal))
        {
            TempData["Message"] = "Invalid optical drive path.";
            return RedirectToAction("Index");
        }

        if (!System.IO.File.Exists(devPath))
            await TryCreateOpticalDeviceNodesAsync(devPath, ct);

        if (!System.IO.File.Exists(devPath))
        {
            TempData["Message"] = $"Drive {devPath} is not currently available. Rescan drives and try again.";
            return RedirectToAction("Index");
        }

        var drive = await db.SystemDrives.FirstOrDefaultAsync(d => d.Mount == devPath, ct);
        if (drive is null)
        {
            TempData["Message"] = $"Drive {devPath} is not registered. Please scan drives first.";
            return RedirectToAction("Index");
        }

        if (string.Equals(drive.DriveMode, "disabled", StringComparison.OrdinalIgnoreCase))
        {
            TempData["Message"] = $"Drive {devPath} is disabled. Enable it before starting a rip.";
            return RedirectToAction("Index");
        }

        // Check if a job is already actively ripping on this device.
        // Jobs that are transcoding (or in other non-ripping states) don't
        // block the drive — a new rip can be started for the next disc.
        var existingJob = await db.Jobs
            .Where(j => j.DevPath == devPath
                && j.Status != JobState.Success
                && j.Status != JobState.Failure
                && j.Status != JobState.Cancelled)
            .OrderByDescending(j => j.StartTime)
            .FirstOrDefaultAsync(ct);

        if (existingJob is not null && existingJob.Status.IsRippingState())
            return RedirectToAction("JobDetail", "Jobs", new { jobId = existingJob.Id });

        // Record the max existing job ID before starting, so we can find the new one
        var maxIdBefore = await db.Jobs.MaxAsync(j => (int?)j.Id, ct) ?? 0;

        backgroundRip.StartRip(devPath);

        // Poll for the new job to appear in DB (up to 5 seconds)
        for (var i = 0; i < 20; i++)
        {
            await Task.Delay(250, ct);
            var job = await db.Jobs
                .Where(j => j.Id > maxIdBefore && j.DevPath == devPath)
                .OrderByDescending(j => j.Id)
                .FirstOrDefaultAsync(ct);

            if (job is not null)
                return RedirectToAction("JobDetail", "Jobs", new { jobId = job.Id });
        }

        TempData["Message"] = $"Rip started for {devPath}. Job page will appear shortly.";
        return RedirectToAction("Index");
    }

    private async Task TryCreateOpticalDeviceNodesAsync(string devPath, CancellationToken ct = default)
    {
        try
        {
            var devName = Path.GetFileName(devPath);
            if (string.IsNullOrWhiteSpace(devName) || !devName.StartsWith("sr", StringComparison.Ordinal))
                return;

            var sysDevPath = $"/sys/class/block/{devName}/dev";
            if (!System.IO.File.Exists(sysDevPath))
                return;

            var nums = (await System.IO.File.ReadAllTextAsync(sysDevPath, ct)).Trim();
            var parts = nums.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length != 2)
                return;

            await runner.RunAsync("mknod", $"{devPath} b {parts[0]} {parts[1]}", timeoutMs: 5_000);
            await runner.RunAsync("chgrp", $"cdrom {devPath}", timeoutMs: 5_000);
            await runner.RunAsync("chmod", $"660 {devPath}", timeoutMs: 5_000);

            var sgBase = $"/sys/class/block/{devName}/device/scsi_generic";
            if (!Directory.Exists(sgBase))
                return;

            var sgEntry = Directory.EnumerateDirectories(sgBase).FirstOrDefault();
            if (sgEntry is null)
                return;

            var sgName = Path.GetFileName(sgEntry);
            var sgDevPath = $"/dev/{sgName}";
            var sysSgDev = $"/sys/class/scsi_generic/{sgName}/dev";
            if (!System.IO.File.Exists(sysSgDev))
                return;

            var sgNums = (await System.IO.File.ReadAllTextAsync(sysSgDev, ct)).Trim();
            var sgParts = sgNums.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (sgParts.Length != 2)
                return;

            await runner.RunAsync("mknod", $"{sgDevPath} c {sgParts[0]} {sgParts[1]}", timeoutMs: 5_000);
            await runner.RunAsync("chgrp", $"cdrom {sgDevPath}", timeoutMs: 5_000);
            await runner.RunAsync("chmod", $"660 {sgDevPath}", timeoutMs: 5_000);
        }
        catch
        {
            // Best-effort recovery in devcontainers where udev is not running.
        }
    }

    [HttpPost("save-ui")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveUi(string theme, int refreshRate, string iconStyle, CancellationToken ct = default)
    {
        var ui = await db.UiSettings.FirstOrDefaultAsync(ct);
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
        await db.SaveChangesAsync(ct);
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
    public async Task<IActionResult> RefreshSysInfo(CancellationToken ct = default)
    {
        var existing = await db.SystemInfos.FirstOrDefaultAsync(ct);
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
        await db.SaveChangesAsync(ct);
        TempData["Message"] = "System info updated.";
        return RedirectToAction("Index");
    }

    [HttpPost("save-notifications")]
    public async Task<IActionResult> SaveNotifications(
        string? PbKey, string? IftttKey, string? PoUserKey,
        string? BashScript, string? JsonUrl, string? Apprise,
        string? ArmApiKey,
        CancellationToken ct = default)
    {
        var fields = new Dictionary<string, string?>
        {
            ["PbKey"] = PbKey is not null ? JsonSerialize(PbKey) : null,
            ["IftttKey"] = IftttKey is not null ? JsonSerialize(IftttKey) : null,
            ["PoUserKey"] = PoUserKey is not null ? JsonSerialize(PoUserKey) : null,
            ["BashScript"] = BashScript is not null ? JsonSerialize(BashScript) : null,
            ["JsonUrl"] = JsonUrl is not null ? JsonSerialize(JsonUrl) : null,
            ["Apprise"] = Apprise is not null ? JsonSerialize(Apprise) : null,
            ["ArmApiKey"] = ArmApiKey is not null ? JsonSerialize(ArmApiKey) : null,
        };

        await SettingsHelper.MergeIntoDbAsync(db, fields, ct);
        TempData["Message"] = "Notification settings saved.";
        TempData["ActiveTab"] = "tab6";
        return RedirectToAction("Index");
    }
}
