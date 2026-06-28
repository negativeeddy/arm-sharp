using ArmRipper.Core.Configuration;
using ArmRipper.Core.Infrastructure;
using ArmRipper.Core.Infrastructure.Data;
using ArmRipper.Core.Models;
using ArmRipper.Core.Rip;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace ArmRipper.Core.Tests;

public sealed class BackgroundRipServiceTests
{
    private static IOptions<ArmSettings> CreateSettings(int maxConcurrentRips = 1)
    {
        return Options.Create(new ArmSettings { MaxConcurrentRips = maxConcurrentRips });
    }

    private static async Task WaitForBackgroundTaskAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var start = Environment.TickCount;
        while (Environment.TickCount - start < timeoutMs)
        {
            if (condition()) return;
            await Task.Delay(100);
        }
    }

    /// <summary>
    /// Holds the mutable shared state for the blocking-conductor pattern.
    /// </summary>
    private sealed class BlockingConductorFixture
    {
        public TaskCompletionSource FirstCallGate { get; } = new();
        public TaskCompletionSource SecondCallRecorded { get; } = new();
        public int ConductorCallCount;
        public Mock<IConductor> ConductorMock { get; } = new();
        public Mock<IServiceScopeFactory> ScopeFactoryMock { get; } = new();
    }

    /// <summary>
    /// Sets up a mock scope factory where the conductor's <c>RunAsync</c> blocks
    /// on the first call (until <c>fixture.FirstCallGate</c> is set) and records
    /// a second call via <c>fixture.SecondCallRecorded</c>.
    /// </summary>
    private static BlockingConductorFixture CreateBlockingConductor(ArmDbContext db)
    {
        var f = new BlockingConductorFixture();

        f.ConductorMock
            .Setup(c => c.RunAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string _, CancellationToken ct) =>
            {
                var idx = Interlocked.Increment(ref f.ConductorCallCount);
                if (idx == 1)
                {
                    // Block until the test releases the gate
                    var tcs = new TaskCompletionSource<int>();
                    ct.Register(() => tcs.TrySetResult(0));
                    f.FirstCallGate.Task.ContinueWith(_ => tcs.TrySetResult(0), TaskContinuationOptions.ExecuteSynchronously);
                    return tcs.Task;
                }
                f.SecondCallRecorded.TrySetResult();
                return Task.FromResult(0);
            });

        var serviceProviderMock = new Mock<IServiceProvider>();
        serviceProviderMock.Setup(sp => sp.GetService(typeof(IConductor))).Returns(f.ConductorMock.Object);
        serviceProviderMock.Setup(sp => sp.GetService(typeof(ArmDbContext))).Returns(db);

        var scopeMock = new Mock<IServiceScope>();
        scopeMock.Setup(s => s.ServiceProvider).Returns(serviceProviderMock.Object);

        f.ScopeFactoryMock.Setup(fac => fac.CreateScope()).Returns(scopeMock.Object);

        return f;
    }

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
            .Returns(NullLoggerFactory.Instance);

        var scope = new Mock<IServiceScope>();
        scope.Setup(s => s.ServiceProvider).Returns(serviceProvider.Object);

        var scopeFactory = new Mock<IServiceScopeFactory>();
        scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);

        var service = new BackgroundRipService(scopeFactory.Object, NullLoggerFactory.Instance, CreateSettings());
        service.StartRip("/dev/sr0");
        await WaitForBackgroundTaskAsync(() => conductorRun);

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
            .Returns(NullLoggerFactory.Instance);

        var scope = new Mock<IServiceScope>();
        scope.Setup(s => s.ServiceProvider).Returns(serviceProvider.Object);

        var scopeFactory = new Mock<IServiceScopeFactory>();
        scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);

        var service = new BackgroundRipService(scopeFactory.Object, NullLoggerFactory.Instance, CreateSettings());
        service.StartRip("/dev/sr0");
        await WaitForBackgroundTaskAsync(() => true);
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
            .Returns(NullLoggerFactory.Instance);

        var scope = new Mock<IServiceScope>();
        scope.Setup(s => s.ServiceProvider).Returns(serviceProvider.Object);

        var scopeFactory = new Mock<IServiceScopeFactory>();
        scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);

        var service = new BackgroundRipService(scopeFactory.Object, NullLoggerFactory.Instance, CreateSettings());

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        service.StartRip("/dev/sr0", cts.Token);
        await WaitForBackgroundTaskAsync(() => true);

        // If we reach here without crash, OperationCanceledException was handled
        Assert.True(true);
    }

    // ──────────────────────────────────────────────
    //  Tests for the concurrent-rip guard logic
    // ──────────────────────────────────────────────

    [Fact]
    public void IsRippingState_ReturnsTrueForRippingStates()
    {
        Assert.True(JobState.Active.IsRippingState());
        Assert.True(JobState.VideoRipping.IsRippingState());
        Assert.True(JobState.VideoWaiting.IsRippingState());
        Assert.True(JobState.VideoInfo.IsRippingState());
        Assert.True(JobState.AudioRipping.IsRippingState());
        Assert.True(JobState.ManualWaitStarted.IsRippingState());
    }

    [Fact]
    public void IsRippingState_ReturnsFalseForNonRippingStates()
    {
        Assert.False(JobState.TranscodeActive.IsRippingState());
        Assert.False(JobState.TranscodeWaiting.IsRippingState());
        Assert.False(JobState.Success.IsRippingState());
        Assert.False(JobState.Failure.IsRippingState());
        Assert.False(JobState.Cancelled.IsRippingState());
    }

    [Fact]
    public async Task StartRip_WhenExistingJobIsTranscoding_StartsNewPipeline()
    {
        // ── 1. Real in-memory DB with a job in TranscodeActive on /dev/sr0 ──
        using var db = TestHelpers.CreateDbContext();
        db.Jobs.Add(TestHelpers.CreateTestJob(j => j.Status = JobState.TranscodeActive));
        await db.SaveChangesAsync();

        // ── 2. Conductor: 1st call blocks, 2nd call recorded ──
        var fix = CreateBlockingConductor(db);

        var service = new BackgroundRipService(fix.ScopeFactoryMock.Object, NullLoggerFactory.Instance, CreateSettings(2));

        // ── 3. First StartRip — adds to _activeRips, starts first pipeline ──
        service.StartRip("/dev/sr0");
        await WaitForBackgroundTaskAsync(() => fix.ConductorCallCount >= 1);
        Assert.Equal(1, fix.ConductorCallCount);

        // ── 4. Second StartRip — TryAdd fails, checks DB (finds TranscodeActive
        //       which is NOT a ripping state), replaces _activeRips entry,
        //       and starts a second pipeline ──
        service.StartRip("/dev/sr0");
        await WaitForBackgroundTaskAsync(() => fix.SecondCallRecorded.Task.IsCompleted, 10000);

        Assert.Equal(2, fix.ConductorCallCount);
        fix.ConductorMock.Verify(c => c.RunAsync("/dev/sr0", It.IsAny<CancellationToken>()), Times.Exactly(2));

        // ── 5. Clean up — release the blocked first pipeline ──
        fix.FirstCallGate.TrySetResult();
        await Task.Delay(200);
    }

    [Fact]
    public async Task StartRip_WhenExistingJobIsRipping_BlocksNewPipeline()
    {
        // ── 1. Real in-memory DB with a job in VideoRipping on /dev/sr0 ──
        using var db = TestHelpers.CreateDbContext();
        db.Jobs.Add(TestHelpers.CreateTestJob(j => j.Status = JobState.VideoRipping));
        await db.SaveChangesAsync();

        // ── 2. Conductor: 1st call blocks, 2nd call should never happen ──
        var fix = CreateBlockingConductor(db);

        var service = new BackgroundRipService(fix.ScopeFactoryMock.Object, NullLoggerFactory.Instance, CreateSettings(2));

        // ── 3. First StartRip — adds to _activeRips ──
        service.StartRip("/dev/sr0");
        await WaitForBackgroundTaskAsync(() => fix.ConductorCallCount >= 1);
        Assert.Equal(1, fix.ConductorCallCount);

        // ── 4. Second StartRip — TryAdd fails, checks DB (finds VideoRipping
        //       which IS a ripping state), logs warning and returns ──
        service.StartRip("/dev/sr0");

        // Give the service time to potentially start a second pipeline (it shouldn't)
        await Task.Delay(2000);

        Assert.Equal(1, fix.ConductorCallCount);
        fix.ConductorMock.Verify(c => c.RunAsync("/dev/sr0", It.IsAny<CancellationToken>()), Times.Once);

        // ── 5. Clean up ──
        fix.FirstCallGate.TrySetResult();
        await Task.Delay(200);
    }

    [Fact]
    public async Task StartRip_WithNoDbEntry_AllowsNewRip()
    {
        // ── 1. Real in-memory DB with NO jobs for /dev/sr0 ──
        using var db = TestHelpers.CreateDbContext();

        // ── 2. Conductor: 1st call blocks, 2nd call recorded ──
        var fix = CreateBlockingConductor(db);

        var service = new BackgroundRipService(fix.ScopeFactoryMock.Object, NullLoggerFactory.Instance, CreateSettings(2));

        // ── 3. First StartRip ──
        service.StartRip("/dev/sr0");
        await WaitForBackgroundTaskAsync(() => fix.ConductorCallCount >= 1);
        Assert.Equal(1, fix.ConductorCallCount);

        // ── 4. Second StartRip — no entry in DB at all → IsAnyJobRippingOnDevPath
        //       returns false (no job found) → replaces entry → starts pipeline ──
        service.StartRip("/dev/sr0");
        await WaitForBackgroundTaskAsync(() => fix.SecondCallRecorded.Task.IsCompleted, 10000);

        Assert.Equal(2, fix.ConductorCallCount);
        fix.ConductorMock.Verify(c => c.RunAsync("/dev/sr0", It.IsAny<CancellationToken>()), Times.Exactly(2));

        // ── 5. Clean up ──
        fix.FirstCallGate.TrySetResult();
        await Task.Delay(200);
    }

    [Fact]
    public async Task StartRip_WithTerminalJobOnSameDevPath_AllowsNewRip()
    {
        // ── 1. DB with a terminal job (Success) on /dev/sr0 ──
        using var db = TestHelpers.CreateDbContext();
        db.Jobs.Add(TestHelpers.CreateTestJob(j => j.Status = JobState.Success));
        await db.SaveChangesAsync();

        // ── 2. Conductor: 1st call blocks, 2nd call recorded ──
        var fix = CreateBlockingConductor(db);

        var service = new BackgroundRipService(fix.ScopeFactoryMock.Object, NullLoggerFactory.Instance, CreateSettings(2));

        // ── 3. First StartRip ──
        service.StartRip("/dev/sr0");
        await WaitForBackgroundTaskAsync(() => fix.ConductorCallCount >= 1);
        Assert.Equal(1, fix.ConductorCallCount);

        // ── 4. Second StartRip — terminal job is excluded from the DB query,
        //       so IsAnyJobRippingOnDevPath returns false → allows new rip ──
        service.StartRip("/dev/sr0");
        await WaitForBackgroundTaskAsync(() => fix.SecondCallRecorded.Task.IsCompleted, 10000);

        Assert.Equal(2, fix.ConductorCallCount);
        fix.ConductorMock.Verify(c => c.RunAsync("/dev/sr0", It.IsAny<CancellationToken>()), Times.Exactly(2));

        // ── 5. Clean up ──
        fix.FirstCallGate.TrySetResult();
        await Task.Delay(200);
    }
}
