using System.Collections.Concurrent;
using ArmRipper.Core.Rip;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArmRipper.Core.Infrastructure;

public sealed class BackgroundRipService(IServiceScopeFactory scopeFactory, ILogger<BackgroundRipService> logger) : IBackgroundRipService
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeRips = new();

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
