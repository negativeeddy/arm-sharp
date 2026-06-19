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
