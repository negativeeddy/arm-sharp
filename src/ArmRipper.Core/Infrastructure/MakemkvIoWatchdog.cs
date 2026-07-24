using ArmRipper.Core.Configuration;
using ArmRipper.Core.Infrastructure.Data;
using ArmRipper.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArmRipper.Core.Infrastructure;

/// <summary>
/// Background service that monitors <c>makemkvcon</c> processes' I/O consumption
/// and cancels jobs whose read I/O exceeds a configured threshold.
///
/// When a scratched or damaged disc causes MakeMKV to enter a read retry storm,
/// I/O can balloon to hundreds of gigabytes. This watchdog detects that condition
/// and terminates the offending process to prevent resource starvation of the
/// container and the ASP.NET request pipeline.
/// </summary>
public sealed class MakemkvIoWatchdog : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IBackgroundRipService _backgroundRipService;
    private readonly IOptions<ArmSettings> _settings;
    private readonly ILogger<MakemkvIoWatchdog> _logger;

    public MakemkvIoWatchdog(
        IServiceScopeFactory scopeFactory,
        IBackgroundRipService backgroundRipService,
        IOptions<ArmSettings> settings,
        ILogger<MakemkvIoWatchdog> logger)
    {
        _scopeFactory = scopeFactory;
        _backgroundRipService = backgroundRipService;
        _settings = settings;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            _logger.LogWarning("MakemkvIoWatchdog requires Linux — I/O monitoring disabled");
            return;
        }

        var maxReadBytes = _settings.Value.MakemkvMaxReadBytes;
        if (maxReadBytes <= 0)
        {
            _logger.LogInformation(
                "MakemkvIoWatchdog disabled (MakemkvMaxReadBytes = {Value})", maxReadBytes);
            return;
        }

        var intervalSec = Math.Max(10, _settings.Value.MakemkvIoWatchdogIntervalSeconds);
        _logger.LogInformation(
            "MakemkvIoWatchdog started (maxReadBytes={MaxBytes}, interval={Interval}s)",
            maxReadBytes, intervalSec);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(intervalSec), stoppingToken);
                await CheckAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MakemkvIoWatchdog iteration failed");
            }
        }
    }

    private async Task CheckAsync(CancellationToken ct)
    {
        // Skip scanning when there are no active rips — nothing to monitor.
        if (_backgroundRipService.ActiveCount == 0)
            return;

        // 1. Find all makemkvcon processes currently running.
        var makemkvPids = FindMakemkvProcesses();
        if (makemkvPids.Count == 0)
            return;

        // 2. Read I/O stats for each process.
        var pidStats = new Dictionary<int, long>();
        foreach (var pid in makemkvPids)
        {
            var readBytes = ReadProcessIoBytes(pid);
            if (readBytes.HasValue)
                pidStats[pid] = readBytes.Value;
        }

        if (pidStats.Count == 0)
            return;

        var threshold = _settings.Value.MakemkvMaxReadBytes;

        // 3. Check each process against the threshold.
        foreach (var (pid, readBytes) in pidStats)
        {
            if (readBytes <= threshold)
            {
                _logger.LogDebug(
                    "makemkvcon PID {Pid}: {ReadBytes:N0} bytes read (threshold: {Threshold:N0})",
                    pid, readBytes, threshold);
                continue;
            }

            _logger.LogWarning(
                "makemkvcon PID {Pid} exceeded I/O threshold: {ReadBytes:N0} bytes read (limit: {Threshold:N0})",
                pid, readBytes, threshold);

            // 4. Find the associated job and cancel it.
            await CancelJobForProcessAsync(pid, readBytes, ct);
        }
    }

    /// <summary>
    /// Enumerates all processes on the system whose <c>comm</c> (process name)
    /// is <c>makemkvcon</c>.
    /// </summary>
    private static List<int> FindMakemkvProcesses()
    {
        var pids = new List<int>();
        try
        {
            foreach (var procDir in Directory.EnumerateDirectories("/proc"))
            {
                var dirName = Path.GetFileName(procDir);
                if (!int.TryParse(dirName, out var pid))
                    continue;

                try
                {
                    var commPath = Path.Combine(procDir, "comm");
                    if (!File.Exists(commPath))
                        continue;

                    var comm = File.ReadAllText(commPath).Trim();
                    if (string.Equals(comm, "makemkvcon", StringComparison.OrdinalIgnoreCase))
                        pids.Add(pid);
                }
                catch
                {
                    // Process may have exited between enumeration and read — skip.
                }
            }
        }
        catch (Exception ex)
        {
            // /proc not accessible (extremely unlikely on Linux, but handle gracefully).
            System.Diagnostics.Debug.WriteLine($"Failed to enumerate /proc: {ex.Message}");
        }

        return pids;
    }

    /// <summary>
    /// Reads the <c>read_bytes</c> counter from <c>/proc/[pid]/io</c>.
    /// This counter tracks the number of bytes this process has actually read
    /// from block storage (physical I/O, not just page cache hits).
    /// Returns <c>null</c> if the process no longer exists or the file is unreadable.
    /// </summary>
    private static long? ReadProcessIoBytes(int pid)
    {
        try
        {
            var ioPath = Path.Combine("/proc", pid.ToString(), "io");
            if (!File.Exists(ioPath))
                return null;

            var lines = File.ReadAllLines(ioPath);
            foreach (var line in lines)
            {
                // Format: "read_bytes: 123456789"
                if (line.StartsWith("read_bytes:", StringComparison.Ordinal))
                {
                    var valueStr = line.AsSpan("read_bytes:".Length).Trim();
                    if (long.TryParse(valueStr, out var bytes))
                        return bytes;
                }
            }
        }
        catch
        {
            // Process likely exited between enumeration and read.
        }

        return null;
    }

    /// <summary>
    /// Extracts the optical device path (e.g., <c>/dev/sr0</c>) from a
    /// <c>makemkvcon</c> process's command line by looking for an argument
    /// starting with <c>dev:</c>.
    /// </summary>
    private static string? GetDevicePathFromProcess(int pid)
    {
        try
        {
            var cmdlinePath = Path.Combine("/proc", pid.ToString(), "cmdline");
            if (!File.Exists(cmdlinePath))
                return null;

            var cmdline = File.ReadAllText(cmdlinePath);
            // Arguments are separated by null bytes in /proc/[pid]/cmdline.
            var args = cmdline.Split('\0', StringSplitOptions.RemoveEmptyEntries);

            foreach (var arg in args)
            {
                if (arg.StartsWith("dev:", StringComparison.Ordinal))
                    return arg[4..]; // Extract "/dev/sr0" from "dev:/dev/sr0"
            }
        }
        catch
        {
            // Process may have exited.
        }

        return null;
    }

    /// <summary>
    /// Finds the active job associated with the given <c>makemkvcon</c> PID and
    /// cancels it via <see cref="IBackgroundRipService.CancelRip"/>.
    /// Falls back to directly killing the process if no matching job is found.
    /// </summary>
    private async Task CancelJobForProcessAsync(int pid, long readBytes, CancellationToken ct)
    {
        // Parse the process command line to find which optical drive it's using.
        var devPath = GetDevicePathFromProcess(pid);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ArmDbContext>();

        if (devPath is not null)
        {
            // Look up the active job on this device.
            var job = await db.Jobs
                .Where(j => j.DevPath == devPath
                    && j.Status != JobState.Success
                    && j.Status != JobState.Failure
                    && j.Status != JobState.Cancelled
                    && j.Status != JobState.Stopping
                    && j.Stage == RipStage.Rip)
                .FirstOrDefaultAsync(ct);

            if (job is not null)
            {
                var threshold = _settings.Value.MakemkvMaxReadBytes;
                _logger.LogWarning(
                    "Cancelling job {JobId} ({Title}) on {DevPath} — " +
                    "makemkvcon PID {Pid} exceeded I/O threshold: {ReadBytes:N0} bytes (limit: {Threshold:N0})",
                    job.Id, job.Title, job.DevPath, pid, readBytes, threshold);

                // Save the error reason before cancellation so it survives
                // the Conductor's catch block (which sets Status/ProgressMessage).
                job.Errors = $"Cancelled by I/O watchdog: makemkvcon PID {pid} " +
                    $"read {readBytes:N0} bytes (limit: {threshold:N0})";
                job.ProgressMessage = "Cancelled — makemkvcon I/O exceeded threshold";
                await db.SaveChangesAsync(ct);

                // Cancel via BackgroundRipService. This triggers the CTS token
                // for this devPath, which cascades to the Conductor's
                // OperationCanceledException handler and ultimately kills
                // the makemkvcon process via CliProcessRunner's ct.Register callback.
                _backgroundRipService.CancelRip(devPath);
                return;
            }

            _logger.LogWarning(
                "No active rip job found for device {DevPath} (makemkvcon PID {Pid})",
                devPath, pid);
        }
        else
        {
            _logger.LogWarning(
                "Could not determine device path from makemkvcon PID {Pid} command line", pid);
        }

        // Fallback: kill the process directly. Since we can't associate it with
        // a job, this at least frees the I/O bandwidth.
        _logger.LogWarning(
            "Directly killing makemkvcon PID {Pid} — no matching job found", pid);
        KillProcess(pid);
    }

    /// <summary>Sends SIGTERM to the given process.</summary>
    private static void KillProcess(int pid)
    {
        try
        {
            using var proc = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "kill",
                    Arguments = $"-TERM {pid}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            proc.Start();
            proc.WaitForExit(5000);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to kill PID {pid}: {ex.Message}");
        }
    }
}
