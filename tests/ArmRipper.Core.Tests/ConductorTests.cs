using ArmRipper.Core.Infrastructure;
using ArmRipper.Core.Infrastructure.Data;
using ArmRipper.Core.Models;
using ArmRipper.Core.Notifications;
using ArmRipper.Core.Rip;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ArmRipper.Core.Tests;

public sealed class ConductorTests : IDisposable
{
    private readonly ArmDbContext _db;

    public ConductorTests()
    {
        _db = TestHelpers.CreateDbContext();
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    [Fact]
    public async Task RunAsync_WithValidDevice_CreatesJobAndConfig()
    {
        var runner = new CliProcessRunner(NullLogger<CliProcessRunner>.Instance);
        var identifyMock = new MockIdentifyService();
        var armRipperMock = new MockArmRipperService();
        var musicBrainzMock = new Mock<IMusicBrainzService>();
        musicBrainzMock.Setup(m => m.IdentifyAsync(It.IsAny<Job>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("");
        var notificationService = new NotificationService(
            NullLogger<NotificationService>.Instance, _db, runner, []);

        var tmpDir = Path.Combine(Path.GetTempPath(), "arm-test", Guid.NewGuid().ToString());
        var conductor = new Conductor(
            NullLogger<Conductor>.Instance,
            _db,
            runner,
            TestHelpers.CreateOptions(a => {
                a.RawPath = Path.Combine(tmpDir, "raw");
                a.TranscodePath = Path.Combine(tmpDir, "transcode");
                a.CompletedPath = Path.Combine(tmpDir, "completed");
                a.LogPath = Path.Combine(tmpDir, "logs");
            }),
            identifyMock,
            armRipperMock,
            musicBrainzMock.Object,
            notificationService);

        var exitCode = await conductor.RunAsync("/dev/sr0");

        Assert.Equal(0, exitCode);

        var jobs = _db.Jobs.ToList();
        Assert.NotEmpty(jobs);
        var job = jobs[0];
        Assert.Equal("/dev/sr0", job.DevPath);
        Assert.NotNull(job.Config);
    }

    [Fact]
    public async Task RunAsync_WithUnknownDiscType_ReturnsFailure()
    {
        var runner = new CliProcessRunner(NullLogger<CliProcessRunner>.Instance);
        var identifyMock = new MockIdentifyService(resultType: DiscType.Unknown);
        var armRipperMock = new MockArmRipperService();
        var musicBrainzMock = new Mock<IMusicBrainzService>();

        var tmpDir = Path.Combine(Path.GetTempPath(), "arm-test", Guid.NewGuid().ToString());
        var conductor = new Conductor(
            NullLogger<Conductor>.Instance,
            _db,
            runner,
            TestHelpers.CreateOptions(a => {
                a.RawPath = Path.Combine(tmpDir, "raw");
                a.TranscodePath = Path.Combine(tmpDir, "transcode");
                a.CompletedPath = Path.Combine(tmpDir, "completed");
                a.LogPath = Path.Combine(tmpDir, "logs");
            }),
            identifyMock,
            armRipperMock,
            musicBrainzMock.Object,
            new NotificationService(NullLogger<NotificationService>.Instance, _db, runner, []));

        var exitCode = await conductor.RunAsync("/dev/sr0");

        Assert.Equal(1, exitCode);
    }

    private sealed class MockIdentifyService(DiscType resultType = DiscType.Dvd) : IIdentifyService
    {
        public Task IdentifyAsync(Job job, CancellationToken ct = default)
        {
            job.DiscType = resultType;
            if (resultType == DiscType.Dvd || resultType == DiscType.Bluray)
            {
                job.Title = "Test Movie";
                job.TitleAuto = "Test Movie";
                job.Year = "2024";
                job.VideoType = "movie";
                job.HasNiceTitle = true;
            }
            return Task.CompletedTask;
        }
    }

    private sealed class MockArmRipperService : IArmRipperService
    {
        public Task<string> RipVisualMediaAsync(Job job, string logFile, bool hasDupes, bool protection, CancellationToken ct = default)
        {
            job.Status = JobState.Success;
            job.Path = "/opt/arm/completed/movies/Test Movie (2024)";
            return Task.FromResult(job.Path);
        }
    }
}
