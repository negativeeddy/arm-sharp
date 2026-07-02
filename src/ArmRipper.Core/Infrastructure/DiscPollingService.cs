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
/// Background service that detects optical disc insertion/removal events.
///
/// On Linux it uses an <see cref="UeventMonitor"/> (AF_NETLINK / NETLINK_KOBJECT_UEVENT)
/// for instant, event-driven notifications — no polling delay.
/// A periodic safety sync (at the configured poll interval) catches any missed events.
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

    /// <summary>Known device states: dev name ("sr0") → sector count (≤0 = no media).</summary>
    private readonly ConcurrentDictionary<string, long> _deviceStates = new(StringComparer.Ordinal);

    /// <summary>When the last uevent fired for a device.  Used to let the drive
    /// settle before re-reading sysfs.</summary>
    private readonly ConcurrentDictionary<string, DateTime> _lastEventAt = new(StringComparer.Ordinal);

    /// <summary>True after the initial scan completes.  Prevents sysfs-stale
    /// data from triggering a rip on startup.</summary>
    private volatile bool _initialScanDone;

    /// <summary>Seconds to wait after a uevent before trusting sysfs readings.</summary>
    private static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(3);

    /// <summary>Optional netlink uevent monitor (Linux only).</summary>
    private UeventMonitor? _monitor;

    // ── Startup / main loop ─────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pollInterval = TimeSpan.FromSeconds(Math.Max(1, _settings.Value.DiscPollIntervalSeconds));

        _logger.LogInformation(
            "DiscPollingService started (poll interval: {Interval}s, enabled: {Enabled})",
            pollInterval.TotalSeconds, _settings.Value.DiscPollingEnabled);

        if (!_settings.Value.DiscPollingEnabled)
        {
            _logger.LogInformation("Disc polling is disabled via configuration");
            return;
        }

        // ── Initial scan ─────────────────────────────────────
        await SyncAllDevicesAsync(stoppingToken);
        _initialScanDone = true;
        _logger.LogInformation("Initial device scan complete — {Count} device(s) found",
            _deviceStates.Count);

        if (_deviceStates.Count > 0)
        {
            foreach (var (dev, size) in _deviceStates)
            {
                _logger.LogDebug("  {Dev}: {Size} sectors ({Label})",
                    dev, size, size > 0 ? "media present" : "no media");
            }
        }

        // ── Start event-driven listener (Linux only) ─────────
        if (OperatingSystem.IsLinux())
        {
            _monitor = await TryStartUeventMonitorAsync(stoppingToken);
            if (_monitor is not null)
            {
                _logger.LogInformation("UeventMonitor active — disc detection is now event-driven");

                // Pump events in the background
#pragma warning disable CA1416 // Platform guard verified above
                _ = Task.Run(() => PumpUeventsAsync(_monitor, stoppingToken), stoppingToken);
#pragma warning restore CA1416
            }
        }

        // ── Main loop: safety sync + pump any queued events ──
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(pollInterval, stoppingToken);
            await SyncAllDevicesAsync(stoppingToken);
        }
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
                if (msg.Subsystem != "block" || !msg.IsMediaChange)
                    continue;

                if (msg.DevName is null || !msg.DevName.StartsWith("sr"))
                    continue;

                var devPath = $"/dev/{msg.DevName}";
                var devName = msg.DevName;
                _logger.LogDebug("Uevent: {Action} on {Dev} (media change)", msg.Action, devPath);

                // Record the event time and clear the cached state so the
                // next poll cycle re-reads sysfs (after a settle delay).
                _lastEventAt[devName] = DateTime.UtcNow;
                _deviceStates.TryRemove(devName, out _);
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Uevent pump exiting — falling back to polling only");
        }
    }

    // ─────────────────────────────────────────────────────────
    //  Device sync
    // ─────────────────────────────────────────────────────────

    /// <summary>Enumerate all /dev/sr* devices and check for state changes.</summary>
    private async Task SyncAllDevicesAsync(CancellationToken ct)
    {
        string[] devices;
        try
        {
            devices = Directory.GetFiles("/dev", "sr*");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enumerate /dev/sr* devices");
            return;
        }

        // Check each existing device
        foreach (var devPath in devices)
            await CheckDeviceAsync(devPath, ct);

        // Prune devices that no longer exist
        var currentSet = new HashSet<string>(devices.Select(d => Path.GetFileName(d)!));
        foreach (var key in _deviceStates.Keys)
        {
            if (!currentSet.Contains(key))
            {
                _deviceStates.TryRemove(key, out _);
                _lastEventAt.TryRemove(key, out _);
                _logger.LogInformation("Device /dev/{Dev} removed", key);
            }
        }
    }

    /// <summary>
    /// Compare sysfs sector count with the last known value and fire
    /// insertion / removal handlers on transitions.
    ///
    /// After a uevent we wait <see cref="SettleTime"/> (3 s) before reading
    /// sysfs, giving the drive mechanism time to finish cycling so the
    /// kernel cache is fresh.
    /// </summary>
    private async Task CheckDeviceAsync(string devPath, CancellationToken ct)
    {
        var devName = Path.GetFileName(devPath);

        // ── Settle delay ────────────────────────────────────
        if (_lastEventAt.TryGetValue(devName, out var lastEvent))
        {
            var age = DateTime.UtcNow - lastEvent;
            if (age < SettleTime)
            {
                _logger.LogDebug("Device /dev/{Dev} still settling ({Age:F1}s since uevent) — skipping",
                    devName, age.TotalSeconds);
                return;
            }
            _lastEventAt.TryRemove(devName, out _);
        }

        // Read sector count from sysfs — safe, no SCSI commands issued.
        var currentSize = await ReadSysfsSizeAsync(devName, ct);
        var prevSize = _deviceStates.GetValueOrDefault(devName, -1L);

        // First time seeing this device (initial scan or post-uevent reset).
        if (prevSize < 0)
        {
            _deviceStates[devName] = currentSize;

            if (currentSize <= 0)
            {
                _logger.LogDebug("/dev/{Dev}: {Size} sectors (no media)", devName, currentSize);
            }
            else if (_initialScanDone)
            {
                // Post-uevent re-check: media present → insertion
                _logger.LogInformation("Disc detected in /dev/{Dev} ({Size} sectors)", devName, currentSize);
                _ = HandleDiscInsertedAsync(devPath);
            }
            else
            {
                // Initial scan: just record — sysfs may be stale
                _logger.LogDebug("/dev/{Dev}: {Size} sectors (initial scan)", devName, currentSize);
            }
            return;
        }

        if (prevSize == currentSize)
            return;

        // State changed
        _deviceStates[devName] = currentSize;

        if (currentSize > 0 && prevSize <= 0)
        {
            // Media appeared → disc was inserted
            _logger.LogInformation("Disc detected in /dev/{Dev} ({Size} sectors)", devName, currentSize);
            _ = HandleDiscInsertedAsync(devPath);
        }
        else if (currentSize <= 0 && prevSize > 0)
        {
            // Media removed → set cooldown
            backgroundRipService.RecordManualEject(devPath);
            _logger.LogInformation("Disc removed from /dev/{Dev} — cooldown started", devName);
            _ = HandleDiscRemovedAsync(devPath);
        }
        else
        {
            _logger.LogDebug("Size changed for /dev/{Dev}: {Prev} → {Size} sectors (no action)",
                devName, prevSize, currentSize);
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
            _logger.LogDebug(
                "Ignoring insertion event for {DevPath} — no media present ({Sectors} sectors)",
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

                // Try to enrich with udev info
                try
                {
                    var runner = scope.ServiceProvider.GetRequiredService<ICliProcessRunner>();
                    var result = await runner.RunAsync("udevadm",
                        $"info --query=property {devPath}", timeoutMs: 10_000);

                    foreach (var line in result.StdOut.Split('\n'))
                    {
                        if (line.StartsWith("ID_MODEL="))
                            drive.Model = line["ID_MODEL=".Length..].Trim('"');
                        else if (line.StartsWith("ID_SERIAL_SHORT="))
                            drive.SerialId = line["ID_SERIAL_SHORT=".Length..].Trim('"');
                        else if (line.StartsWith("ID_REVISION="))
                            drive.Firmware = line["ID_REVISION=".Length..].Trim('"');
                        else if (line.StartsWith("ID_CDROM_DVD=") && line.EndsWith("1"))
                            drive.ReadDvd = true;
                        else if (line.StartsWith("ID_CDROM_BD=") && line.EndsWith("1"))
                            drive.ReadBd = true;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Could not query udev info for {DevPath}", devPath);
                }

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
    //  Low-level helpers
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
}
