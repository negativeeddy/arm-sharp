using System.Collections.Concurrent;
using System.Runtime.Versioning;
using ArmRipper.Core.Configuration;
using ArmRipper.Core.Infrastructure.Data;
using ArmRipper.Core.Models;
using ArmRipper.Core.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArmRipper.Core.Infrastructure;

/// <summary>
/// Background service that detects optical disc insertion/removal events via
/// kernel uevents (AF_NETLINK / NETLINK_KOBJECT_UEVENT).  Purely event-driven —
/// no polling, no startup scan, no SCSI commands.
///
/// When a disc is inserted on a drive in "autodetect" mode, it automatically kicks off
/// a rip via <see cref="IBackgroundRipService"/>.
/// </summary>
public sealed class DiscPollingService(
    IServiceScopeFactory scopeFactory,
    IOptions<ArmSettings> settings,
    ILoggerFactory loggerFactory,
    INotificationBroadcaster broadcaster,
    IBackgroundRipService backgroundRipService)
    : BackgroundService
{
    private readonly ILogger _logger = loggerFactory.CreateLogger("DiscPollingService");
    private readonly IOptions<ArmSettings> _settings = settings;

    /// <summary>Seconds to wait after a uevent before reading sysfs.</summary>
    private static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(3);

    /// <summary>Optional netlink uevent monitor (Linux only).</summary>
    private UeventMonitor? _monitor;

    /// <summary>Tracks devices currently undergoing a settle+check cycle to
    /// avoid duplicate rips when the kernel fires multiple change uevents
    /// for a single insertion.</summary>
    private readonly ConcurrentDictionary<string, bool> _inflightChecks = new(StringComparer.Ordinal);

    // ── Startup / main loop ─────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "DiscPollingService started (event-driven mode, enabled: {Enabled})",
            _settings.Value.DiscPollingEnabled);

        if (!_settings.Value.DiscPollingEnabled)
        {
            _logger.LogInformation("Disc detection is disabled via configuration");
            return;
        }

        if (!OperatingSystem.IsLinux())
        {
            _logger.LogWarning("UeventMonitor requires Linux — disc detection unavailable");
            return;
        }

        _monitor = await TryStartUeventMonitorAsync(stoppingToken);
        if (_monitor is null)
        {
            _logger.LogWarning("Failed to start UeventMonitor — disc detection unavailable");
            return;
        }

        _logger.LogInformation("UeventMonitor active — disc detection is purely event-driven");

#pragma warning disable CA1416 // Platform guard verified above
        await PumpUeventsAsync(_monitor, stoppingToken);
#pragma warning restore CA1416
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("DiscPollingService stopping");
        await base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        if (OperatingSystem.IsLinux())
        {
            _monitor?.Dispose();
        }
        base.Dispose();
    }

    // ─────────────────────────────────────────────────────────
    //  Uevent listener (Linux)
    // ─────────────────────────────────────────────────────────

    [SupportedOSPlatform("linux")]
    private Task<UeventMonitor?> TryStartUeventMonitorAsync(CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger<UeventMonitor>();
        var monitor = new UeventMonitor(logger);

        if (!monitor.TryStart())
            return Task.FromResult<UeventMonitor?>(null);

        return Task.FromResult<UeventMonitor?>(monitor);
    }

    [SupportedOSPlatform("linux")]
    private async Task PumpUeventsAsync(UeventMonitor monitor, CancellationToken ct)
    {
        try
        {
            await foreach (var msg in monitor.ListenAsync(ct))
            {
                // Log every block uevent for an sr device at Debug level so we can
                // diagnose detection issues from logs.
                if (msg.Subsystem == "block" && msg.DevName?.StartsWith("sr") == true)
                {
                    var dmc = msg.Properties.TryGetValue("DISK_MEDIA_CHANGE", out var d) ? d : "(absent)";
                    _logger.LogInformation(
                        "Uevent: {Action} on /dev/{Dev} (mediaChange={IsMediaChange}, DISK_MEDIA_CHANGE={Dmc})",
                        msg.Action, msg.DevName, msg.IsMediaChange, dmc);
                }

                // We accept ANY change uevent for an sr device, not just those with
                // DISK_MEDIA_CHANGE=1.  The DISK_MEDIA_CHANGE flag is set by udev's
                // cdrom_id built-in which opens the device node — in containers or
                // systems without udev it may never appear.  The sysfs size check
                // in HandleMediaChangeAsync is the authoritative test.
                if (msg.Subsystem != "block")
                    continue;

                if (msg.Action != "change" && !msg.IsMediaChange)
                    continue;

                if (msg.DevName is null || !msg.DevName.StartsWith("sr"))
                    continue;

                var devPath = $"/dev/{msg.DevName}";

                // Dedup: kernel may fire multiple change uevents for one insertion.
                // Only one settle/check cycle per device at a time.
                if (!_inflightChecks.TryAdd(msg.DevName, true))
                {
                    _logger.LogInformation("Settle already in progress for /dev/{Dev} — ignoring duplicate uevent", msg.DevName);
                    continue;
                }

                _logger.LogInformation("Media change detected on {Dev} — starting settle timer", devPath);

                // Handle asynchronously (settle + sysfs verify); release lock when done
                _ = HandleMediaChangeAsync(devPath, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Uevent pump exited unexpectedly");
        }
    }

    [SupportedOSPlatform("linux")]
    private async Task HandleMediaChangeAsync(string devPath, CancellationToken ct)
    {
        var devName = Path.GetFileName(devPath);
        try
        {
            // Let the drive mechanism finish cycling before reading sysfs
            await Task.Delay(SettleTime, ct);

            var size = await ReadSysfsSizeAsync(devName, ct);

            if (size > 0)
            {
                _logger.LogInformation("Disc detected in /dev/{Dev} ({Size} sectors)", devName, size);
                await HandleDiscInsertedAsync(devPath);
            }
            else
            {
                _logger.LogInformation("Disc removed from /dev/{Dev}", devName);
                backgroundRipService.RecordManualEject(devPath);
                await HandleDiscRemovedAsync(devPath);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling media change for {DevPath}", devPath);
        }
        finally
        {
            _inflightChecks.TryRemove(devName, out _);
        }
    }

    // ─────────────────────────────────────────────────────────
    //  Event handlers
    // ─────────────────────────────────────────────────────────

    private async Task HandleDiscInsertedAsync(string devPath)
    {
        // ── Verify media is actually present via sysfs ────────────
        // Uses /sys/block/*/size which reads kernel-cached data without
        // issuing SCSI commands — does NOT close the tray.
        var devName = Path.GetFileName(devPath);
        var verifySectors = await ReadSysfsSizeAsync(devName, CancellationToken.None);
        if (verifySectors <= 0)
        {
            _logger.LogInformation(
                "Ignoring insertion event for {DevPath} — no media present ({Sectors} sectors, likely tray closed without disc)",
                devPath, verifySectors);
            return;
        }

        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ArmDbContext>();

            // ── Find or auto-register the drive ──
            var drive = await db.SystemDrives.FirstOrDefaultAsync(d => d.Mount == devPath);

            if (drive is null)
            {
                drive = new SystemDrive
                {
                    Mount = devPath,
                    Name = devName,
                    DriveMode = "autodetect",
                };

                // Enrich drive info via sysfs — never opens the device node,
                // so no SCSI commands are issued.  This avoids triggering tray
                // closure on drives that close when they receive any command.
                drive.Model = await ReadSysfsStringAsync(devName, "device/model", CancellationToken.None);
                drive.Serial = await ReadSysfsStringAsync(devName, "device/serial", CancellationToken.None);
                drive.Firmware = await ReadSysfsStringAsync(devName, "device/firmware_revision", CancellationToken.None);

                // Capability bits: /sys/block/*/capability is a hex mask
                //   0x02 = CD-ROM, 0x04 = DVD-ROM, 0x100 = BD-ROM
                var caps = await ReadSysfsCapabilityAsync(devName, CancellationToken.None);
                drive.ReadCd = (caps & 0x02) != 0;
                drive.ReadDvd = (caps & 0x04) != 0;
                drive.ReadBd = (caps & 0x100) != 0;

                _logger.LogDebug("Enriched drive /dev/{Dev} from sysfs: Model={Model}, Firmware={Fw}, Caps=0x{Caps:X}",
                    devName, drive.Model, drive.Firmware, caps);

                db.SystemDrives.Add(drive);
                await db.SaveChangesAsync();
                _logger.LogInformation("Auto-registered new drive {DevPath} with mode 'autodetect'", devPath);
            }

            // ── Check drive mode ──
            if (drive.DriveMode != "autodetect")
            {
                _logger.LogInformation(
                    "Drive {DevPath} is in '{Mode}' mode — skipping auto rip",
                    devPath, drive.DriveMode);
                return;
            }

            // ── Broadcast disc-detected notification ──
            try
            {
                await broadcaster.BroadcastAsync(new Notification
                {
                    EventType = "disc_detected",
                    Message = $"Disc detected in {devPath}",
                    Timestamp = DateTime.UtcNow,
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to broadcast disc-detected notification");
            }

            // ── Start the rip ──
            var ripService = scope.ServiceProvider.GetRequiredService<IBackgroundRipService>();
            ripService.StartRip(devPath);
            _logger.LogInformation("Rip initiated for {DevPath}", devPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling disc insertion for {DevPath}", devPath);
        }
    }

    private async Task HandleDiscRemovedAsync(string devPath)
    {
        try
        {
            await broadcaster.BroadcastAsync(new Notification
            {
                EventType = "disc_removed",
                Message = $"Disc removed from {devPath}",
                Timestamp = DateTime.UtcNow,
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to broadcast disc-removed notification");
        }
    }

    // ─────────────────────────────────────────────────────────
    //  Low-level helpers — sysfs (safe, no SCSI commands)
    // ─────────────────────────────────────────────────────────

    /// <summary>
    /// Read the block-device sector count from <c>/sys/block/{devName}/size</c>.
    /// This reads a kernel-cached value from sysfs — it does NOT open the device
    /// node and therefore does NOT issue SCSI commands that could close the tray.
    /// Returns the sector count, or ≤ 0 if no media or the path is unreadable.
    /// </summary>
    private static Task<long> ReadSysfsSizeAsync(string devName, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var path = $"/sys/block/{devName}/size";
        try
        {
            if (!File.Exists(path))
                return Task.FromResult(0L);

            var text = File.ReadAllText(path).Trim();
            if (long.TryParse(text, out var size))
                return Task.FromResult(size);

            return Task.FromResult(0L);
        }
        catch (Exception) when (!(ct.IsCancellationRequested))
        {
            return Task.FromResult(0L);
        }
    }

    /// <summary>
    /// Read a string attribute from <c>/sys/block/{devName}/{subPath}</c>.
    /// All sysfs reads are kernel-cached and do NOT open the device node,
    /// so no SCSI commands are issued.  Returns the trimmed value, or
    /// <c>null</c> if the file is missing or unreadable.
    /// </summary>
    private static async Task<string?> ReadSysfsStringAsync(string devName, string subPath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var path = $"/sys/block/{devName}/{subPath}";
        try
        {
            if (!File.Exists(path))
                return null;

            var content = await File.ReadAllTextAsync(path, ct);
            return content.Trim().Trim('"');
        }
        catch (Exception) when (!(ct.IsCancellationRequested))
        {
            return null;
        }
    }

    /// <summary>
    /// Read the capability bitmask from <c>/sys/block/{devName}/capability</c>.
    /// Returns 0 if the file is missing or unreadable.
    /// </summary>
    private static async Task<int> ReadSysfsCapabilityAsync(string devName, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var path = $"/sys/block/{devName}/capability";
        try
        {
            if (!File.Exists(path))
                return 0;

            var content = await File.ReadAllTextAsync(path, ct);
            return Convert.ToInt32(content.Trim(), 16);
        }
        catch (Exception) when (!(ct.IsCancellationRequested))
        {
            return 0;
        }
    }
}
