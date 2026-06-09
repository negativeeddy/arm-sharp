using ArmRipper.Core.Rip;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArmRipper.Core.Infrastructure;

public sealed class BackgroundRipService(IServiceScopeFactory scopeFactory, ILogger<BackgroundRipService> logger) : IBackgroundRipService
{
    public void StartRip(string devPath, CancellationToken ct = default)
    {
        _ = Task.Run(async () =>
        {
            using var scope = scopeFactory.CreateScope();
            try
            {
                var conductor = scope.ServiceProvider.GetRequiredService<IConductor>();
                await conductor.RunAsync(devPath, ct);
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
        }, ct);
    }
}
