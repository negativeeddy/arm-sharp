using System.Collections.Concurrent;
using ArmRipper.Core.Configuration;
using ArmRipper.Core.Infrastructure.Data;
using ArmRipper.Core.Models;
using ArmRipper.Core.Rip;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArmRipper.Core.Infrastructure;

public sealed class BackgroundRipService(IServiceScopeFactory scopeFactory, ILoggerFactory loggerFactory, IOptions<ArmSettings> settings)
    : IBackgroundRipService
{
    private readonly ILogger logger = loggerFactory.CreateLogger("BackgroundRipService");
    private readonly IOptions<ArmSettings> _settings = settings;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeRips = new();
    private readonly ConcurrentDictionary<string, DateTime> _ejectCooldowns = new();
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private int _activeOperationCount = 0;

    public void StartRip(string devPath, CancellationToken ct = default)
    {
        // ── Eject cooldown: don't start a new rip on a device that just finished ──
        // The eject command runs after the rip completes and can take 15–30 s.
        // During that window the event-driven monitor may detect the disc as
        // "inserted" and fire another rip on the same device.  A configurable
        // cooldown after the previous rip exits prevents this false re-trigger.
        if (_ejectCooldowns.TryGetValue(devPath, out var cooledUntil))
        {
            if (DateTime.UtcNow < cooledUntil)
            {
                logger.LogInformation(
                    "Skipping rip for {DevPath} — still in eject cooldown ({Remaining:F0}s remaining)",
                    devPath, (cooledUntil - DateTime.UtcNow).TotalSeconds);
                return;
            }
            _ejectCooldowns.TryRemove(devPath, out _);
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (!_activeRips.TryAdd(devPath, cts))
        {
            // A pipeline is already running for this devPath. If the existing job(s)
            // on this drive are in a non-ripping state (e.g. transcoding), the drive
            // is free — replace the entry so a new rip can start.
            if (!IsAnyJobRippingOnDevPath(devPath))
            {
                logger.LogInformation(
                    "Existing pipeline for {DevPath} is in transcode or other non-ripping state; " +
                    "replacing entry to allow a new rip", devPath);
                _activeRips.TryRemove(devPath, out _);
                if (_activeRips.TryAdd(devPath, cts))
                    goto start;
            }

            logger.LogWarning("Rip already in progress for {DevPath}", devPath);
            return;
        }

    start:

        _ = Task.Run(async () =>
        {
            using var scope = scopeFactory.CreateScope();

            // Fetch effective settings early so the cooldown duration is available
            // in the finally block even if the rip itself throws.
            ArmSettings? effectiveSettings = null;
            try
            {
                var db = scope.ServiceProvider.GetRequiredService<ArmDbContext>();
                effectiveSettings = await SettingsHelper.GetEffectiveSettingsAsync(db, _settings.Value, cts.Token);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to load effective settings for {DevPath}; using file defaults", devPath);
            }

            effectiveSettings ??= _settings.Value;

            try
            {
                // ── Final sysfs check ─────────────────────────────
                // Even after the cooldown expires, verify via sysfs
                // that media is actually present before touching the
                // drive.  This catches the edge case where sysfs
                // reports stale cached data after cooldown expiry.
                if (!IsMediaPresent(devPath))
                {
                    logger.LogInformation(
                        "Not starting rip for {DevPath} — no media detected via sysfs", devPath);
                    return;
                }

                // ── Acquire concurrency slot ──────────────────────
                // The slot is released in the finally block below so
                // it's guaranteed to be freed even if the conductor or
                // its dependency resolution throws.
                var db = scope.ServiceProvider.GetRequiredService<ArmDbContext>();
                await WaitForOperationSlotAsync(effectiveSettings.MaxConcurrentRips, cts.Token);

                var conductor = scope.ServiceProvider.GetRequiredService<IConductor>();
                await conductor.RunAsync(devPath, cts.Token);
                logger.LogInformation("Background rip completed for {DevPath}", devPath);
            }
            catch (OperationCanceledException)
            {
                logger.LogWarning("Background rip cancelled for {DevPath}", devPath);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Background rip failed for {DevPath}", devPath);
            }
            finally
            {
                ReleaseOperationSlot();
                _activeRips.TryRemove(devPath, out _);

                // Enter eject cooldown so the monitor doesn't re-trigger a rip
                // while the physical disc is still ejecting.
                var cooldownSec = effectiveSettings.EjectCooldownSeconds;
                _ejectCooldowns[devPath] = DateTime.UtcNow.AddSeconds(cooldownSec);
                logger.LogDebug("Eject cooldown started for {DevPath} ({Cooldown}s)",
                    devPath, cooldownSec);

                cts.Dispose();
            }
        }, cts.Token);
    }

    public void StartForkedJob(int originalJobId, string rawFilePath, CancellationToken ct = default)
    {
        var key = $"forked-{originalJobId}-{rawFilePath.GetHashCode()}";
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (!_activeRips.TryAdd(key, cts))
        {
            logger.LogWarning("Forked transcode already in progress for raw path {RawPath}", rawFilePath);
            return;
        }

        _ = Task.Run(async () =>
        {
            using var scope = scopeFactory.CreateScope();
            try
            {
                var db = scope.ServiceProvider.GetRequiredService<ArmDbContext>();
                var effectiveSettings = await SettingsHelper.GetEffectiveSettingsAsync(db, _settings.Value, cts.Token);

                // ── Acquire concurrency slot ──────────────────────
                await WaitForOperationSlotAsync(effectiveSettings.MaxConcurrentRips, cts.Token);

                var conductor = scope.ServiceProvider.GetRequiredService<IConductor>();
                await conductor.RunForkedTranscodeAsync(originalJobId, rawFilePath, cts.Token);
                logger.LogInformation("Forked transcode completed for job {OriginalJobId}, raw path {RawPath}",
                    originalJobId, rawFilePath);
            }
            catch (OperationCanceledException)
            {
                logger.LogWarning("Forked transcode cancelled for job {OriginalJobId}", originalJobId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Forked transcode failed for job {OriginalJobId}", originalJobId);
            }
            finally
            {
                ReleaseOperationSlot();
                _activeRips.TryRemove(key, out _);
                cts.Dispose();
            }
        }, cts.Token);
    }

    public void CancelRip(string devPath)
    {
        if (_activeRips.TryRemove(devPath, out var cts))
        {
            logger.LogInformation("Cancelling rip for {DevPath}", devPath);
            cts.Cancel();
            cts.Dispose();
        }
    }

    public IReadOnlyList<string> CancelAll()
    {
        var paths = new List<string>();
        foreach (var kvp in _activeRips)
        {
            paths.Add(kvp.Key);
            try
            {
                logger.LogInformation("Shutdown: cancelling rip for {Key}", kvp.Key);
                kvp.Value.Cancel();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to cancel rip for {Key}", kvp.Key);
            }
        }
        _activeRips.Clear();
        return paths;
    }

    public int ActiveCount => _activeRips.Count;

    public void RecordManualEject(string devPath)
    {
        // Read the cooldown from settings so we use whatever the user configured
        var cooldownSec = Math.Max(1, _settings.Value.EjectCooldownSeconds);
        _ejectCooldowns[devPath] = DateTime.UtcNow.AddSeconds(cooldownSec);
        logger.LogInformation("Manual eject recorded for {DevPath} — cooldown {Cooldown}s",
            devPath, cooldownSec);
    }

    /// <summary>Check sysfs for media presence without touching the device node.</summary>
    private static bool IsMediaPresent(string devPath)
    {
        try
        {
            var devName = Path.GetFileName(devPath.TrimEnd('/'));
            var path = $"/sys/block/{devName}/size";
            if (!File.Exists(path))
                return false;
            var content = File.ReadAllText(path).Trim();
            return long.TryParse(content, out var size) && size > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Queries the DB to see if any non-terminal job on the given
    /// devPath is in a ripping state (i.e. still using the optical drive).</summary>
    private bool IsAnyJobRippingOnDevPath(string devPath)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ArmDbContext>();
            var rippingJob = db.Jobs
                .Where(j => j.DevPath == devPath
                    && j.Status != JobState.Success
                    && j.Status != JobState.Failure
                    && j.Status != JobState.Cancelled
                    && j.Status != JobState.Stopping)
                .OrderByDescending(j => j.StartTime)
                .FirstOrDefault();

            return rippingJob?.Status.IsRippingState() ?? false;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to check ripping state for {DevPath}; assuming ripping in progress", devPath);
            return true; // safe default — block the new rip
        }
    }

    /// <summary>
    /// Waits until the number of active operations is below <paramref name="maxConcurrent"/>,
    /// then increments the counter and returns. Uses the effective MaxConcurrentRips
    /// setting read from the DB at runtime so that user changes via the UI are respected
    /// immediately without requiring an app restart.
    /// </summary>
    private async Task WaitForOperationSlotAsync(int maxConcurrent, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            await _operationLock.WaitAsync(ct);
            try
            {
                if (_activeOperationCount < maxConcurrent)
                {
                    _activeOperationCount++;
                    return;
                }
            }
            finally
            {
                _operationLock.Release();
            }

            // All slots are busy — wait a short moment before retrying
            await Task.Delay(250, ct);
        }
    }

    /// <summary>
    /// Decrements the active operation counter.  Safe to call even if no slot
    /// was acquired (the counter is clamped to never go negative).
    /// </summary>
    private void ReleaseOperationSlot()
    {
        var newCount = Interlocked.Decrement(ref _activeOperationCount);
        if (newCount < 0)
        {
            // Defensive: clamp to 0.  This can happen if the finally
            // block fires after an early return that never acquired a slot
            // (e.g. IsMediaPresent check).
            Interlocked.Exchange(ref _activeOperationCount, 0);
        }
    }
}
