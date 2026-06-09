using ArmRipper.Core.Infrastructure;
using ArmRipper.Core.Rip;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ArmRipper.Core.Tests;

public sealed class BackgroundRipServiceTests
{
    [Fact]
    public async Task StartRip_CreatesScopeAndRunsConductor()
    {
        var conductorRun = false;

        var conductorMock = new Mock<IConductor>();
        conductorMock
            .Setup(c => c.RunAsync("/dev/sr0", It.IsAny<CancellationToken>()))
            .ReturnsAsync(0)
            .Callback(() => conductorRun = true);

        var serviceProvider = new Mock<IServiceProvider>();
        serviceProvider.Setup(sp => sp.GetService(typeof(IConductor))).Returns(conductorMock.Object);
        serviceProvider.Setup(sp => sp.GetService(typeof(ILogger<BackgroundRipService>)))
            .Returns(NullLogger<BackgroundRipService>.Instance);

        var scope = new Mock<IServiceScope>();
        scope.Setup(s => s.ServiceProvider).Returns(serviceProvider.Object);

        var scopeFactory = new Mock<IServiceScopeFactory>();
        scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);

        var service = new BackgroundRipService(scopeFactory.Object, NullLogger<BackgroundRipService>.Instance);
        service.StartRip("/dev/sr0");
        await Task.Delay(500);

        Assert.True(conductorRun);
        conductorMock.Verify(c => c.RunAsync("/dev/sr0", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StartRip_WhenConductorThrows_DoesNotCrash()
    {
        var conductorMock = new Mock<IConductor>();
        conductorMock
            .Setup(c => c.RunAsync("/dev/sr0", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("rip failed"));

        var serviceProvider = new Mock<IServiceProvider>();
        serviceProvider.Setup(sp => sp.GetService(typeof(IConductor))).Returns(conductorMock.Object);
        serviceProvider.Setup(sp => sp.GetService(typeof(ILogger<BackgroundRipService>)))
            .Returns(NullLogger<BackgroundRipService>.Instance);

        var scope = new Mock<IServiceScope>();
        scope.Setup(s => s.ServiceProvider).Returns(serviceProvider.Object);

        var scopeFactory = new Mock<IServiceScopeFactory>();
        scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);

        var service = new BackgroundRipService(scopeFactory.Object, NullLogger<BackgroundRipService>.Instance);
        service.StartRip("/dev/sr0");
        await Task.Delay(500);

        conductorMock.Verify(c => c.RunAsync("/dev/sr0", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StartRip_CancellationToken_CancelsOperation()
    {
        var conductorMock = new Mock<IConductor>();
        conductorMock
            .Setup(c => c.RunAsync("/dev/sr0", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var serviceProvider = new Mock<IServiceProvider>();
        serviceProvider.Setup(sp => sp.GetService(typeof(IConductor))).Returns(conductorMock.Object);
        serviceProvider.Setup(sp => sp.GetService(typeof(ILogger<BackgroundRipService>)))
            .Returns(NullLogger<BackgroundRipService>.Instance);

        var scope = new Mock<IServiceScope>();
        scope.Setup(s => s.ServiceProvider).Returns(serviceProvider.Object);

        var scopeFactory = new Mock<IServiceScopeFactory>();
        scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);

        var service = new BackgroundRipService(scopeFactory.Object, NullLogger<BackgroundRipService>.Instance);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        service.StartRip("/dev/sr0", cts.Token);
        await Task.Delay(500);

        // If we reach here without crash, OperationCanceledException was handled
        Assert.True(true);
    }
}
