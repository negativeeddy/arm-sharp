using System.Collections.Concurrent;
using ArmRipper.Core.Configuration;
using ArmRipper.Core.Rip;
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
            logger.LogWarning("Rip already in progress for {DevPath}", devPath);
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
}
