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
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeRips = new();
    private readonly SemaphoreSlim _ripSemaphore = new(settings.Value.MaxConcurrentRips, settings.Value.MaxConcurrentRips);

    public void StartRip(string devPath, CancellationToken ct = default)
    {
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
            try
            {
                await _ripSemaphore.WaitAsync(cts.Token);
                try
                {
                    var conductor = scope.ServiceProvider.GetRequiredService<IConductor>();
                    await conductor.RunAsync(devPath, cts.Token);
                    logger.LogInformation("Background rip completed for {DevPath}", devPath);
                }
                finally
                {
                    _ripSemaphore.Release();
                }
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
                _activeRips.TryRemove(devPath, out _);
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
                await _ripSemaphore.WaitAsync(cts.Token);
                try
                {
                    var conductor = scope.ServiceProvider.GetRequiredService<IConductor>();
                    await conductor.RunForkedTranscodeAsync(originalJobId, rawFilePath, cts.Token);
                    logger.LogInformation("Forked transcode completed for job {OriginalJobId}, raw path {RawPath}",
                        originalJobId, rawFilePath);
                }
                finally
                {
                    _ripSemaphore.Release();
                }
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
                    && j.Status != JobState.Cancelled)
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
}
