using ArmRipper.Core.Configuration;
using ArmRipper.Core.Infrastructure;
using ArmRipper.Core.Infrastructure.Data;
using ArmRipper.Core.Models;
using ArmRipper.Core.Notifications;
using ArmRipper.Core.Rip;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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

    private static IOptions<ArmSettings> CreateTestOptions()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), "arm-test", Guid.NewGuid().ToString());
        return TestHelpers.CreateOptions(a =>
        {
            a.RawPath = Path.Combine(tmpDir, "raw");
            a.TranscodePath = Path.Combine(tmpDir, "transcode");
            a.CompletedPath = Path.Combine(tmpDir, "completed");
            a.LogPath = Path.Combine(tmpDir, "logs");
        });
    }

    private static Mock<ICliProcessRunner> CreateMockRunner()
    {
        var mock = new Mock<ICliProcessRunner>();
        mock.Setup(r => r.RunAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CliResult(0, "", "", false));
        return mock;
    }

    private Conductor CreateConductor(
        IIdentifyService? identify = null,
        IArmRipperService? ripper = null,
        IMusicBrainzService? musicBrainz = null,
        IOptions<ArmSettings>? options = null,
        ICliProcessRunner? runner = null)
    {
        runner ??= CreateMockRunner().Object;
        var musicBrainzService = musicBrainz ?? new Mock<IMusicBrainzService>().Object;
        return new Conductor(
            NullLoggerFactory.Instance,
            _db,
            runner,
            options ?? CreateTestOptions(),
            identify ?? new MockIdentifyService(),
            ripper ?? new MockArmRipperService(),
            musicBrainzService,
            new NotificationService(NullLoggerFactory.Instance, _db, runner, Mock.Of<IHttpClientFactory>(), []),
            [],
            new JobFileLoggerProvider());
    }

    [Fact]
    public async Task RunAsync_WithDvd_CreatesJobAndReturnsSuccess()
    {
        var conductor = CreateConductor();
        var exitCode = await conductor.RunAsync("/dev/sr0");

        Assert.Equal(0, exitCode);

        var jobs = _db.Jobs.ToList();
        var job = Assert.Single(jobs);
        Assert.Equal("/dev/sr0", job.DevPath);
        Assert.Equal(JobState.Success, job.Status);
        Assert.NotNull(job.Config);
    }

    [Fact]
    public async Task RunAsync_WithBluray_ReturnsSuccess()
    {
        var conductor = CreateConductor(identify: new MockIdentifyService(DiscType.Bluray));
        var exitCode = await conductor.RunAsync("/dev/sr0");

        Assert.Equal(0, exitCode);
        var job = _db.Jobs.Single();
        Assert.Equal(JobState.Success, job.Status);
        Assert.Equal("Test Movie", job.Title);
    }

    [Fact]
    public async Task RunAsync_WithMusic_ReturnsSuccess()
    {
        var musicBrainzMock = new Mock<IMusicBrainzService>();
        musicBrainzMock.Setup(m => m.IdentifyAsync(It.IsAny<Job>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("Some Album");

        var identifyMock = new MockIdentifyService(DiscType.Music, label: "MyMusicCD");
        var conductor = CreateConductor(identify: identifyMock, musicBrainz: musicBrainzMock.Object);

        var exitCode = await conductor.RunAsync("/dev/sr0");

        Assert.Equal(0, exitCode);
        var job = _db.Jobs.Single();
        Assert.Equal(JobState.Success, job.Status);
    }

    [Fact]
    public async Task RunAsync_WithDataDisc_ReturnsSuccess()
    {
        var identifyMock = new MockIdentifyService(DiscType.Data, label: "MyDataDisc");
        var conductor = CreateConductor(identify: identifyMock);

        var exitCode = await conductor.RunAsync("/dev/sr0");

        Assert.Equal(0, exitCode);
        var job = _db.Jobs.Single();
        Assert.Equal(JobState.Success, job.Status);
    }

    [Fact]
    public async Task RunAsync_WithUnknownDiscType_ReturnsFailure()
    {
        var identifyMock = new MockIdentifyService(resultType: DiscType.Unknown);
        var conductor = CreateConductor(identify: identifyMock);

        var exitCode = await conductor.RunAsync("/dev/sr0");

        Assert.Equal(1, exitCode);
        var job = _db.Jobs.Single();
        Assert.Equal(JobState.Failure, job.Status);
    }

    [Fact]
    public async Task RunAsync_WhenRipVisualFails_MarksJobAsFailure()
    {
        var failingRipper = new Mock<IArmRipperService>();
        failingRipper.Setup(r => r.RipVisualMediaAsync(
                It.IsAny<Job>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("MakeMKV failed"));

        var conductor = CreateConductor(ripper: failingRipper.Object);

        var exitCode = await conductor.RunAsync("/dev/sr0");

        Assert.Equal(1, exitCode);
        var job = _db.Jobs.Single();
        Assert.Equal(JobState.Failure, job.Status);
        Assert.Contains("MakeMKV failed", job.Errors);
    }

    [Fact]
    public async Task RunAsync_WhenMusicBrainzFails_MarksJobAsFailure()
    {
        var musicBrainzMock = new Mock<IMusicBrainzService>();
        musicBrainzMock.Setup(m => m.IdentifyAsync(It.IsAny<Job>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("MusicBrainz API error"));

        var identifyMock = new MockIdentifyService(DiscType.Music, label: "MyMusicCD");
        var conductor = CreateConductor(identify: identifyMock, musicBrainz: musicBrainzMock.Object);

        var exitCode = await conductor.RunAsync("/dev/sr0");

        Assert.Equal(1, exitCode);
        var job = _db.Jobs.Single();
        Assert.Equal(JobState.Failure, job.Status);
        Assert.Contains("MusicBrainz API error", job.Errors);
    }

    [Fact]
    public async Task RunAsync_JobHasConfigSnapshot_WithExpectedDefaults()
    {
        var options = TestHelpers.CreateOptions(a =>
        {
            a.RawPath = "/opt/arm/raw";
            a.TranscodePath = "/opt/arm/transcode";
            a.CompletedPath = "/opt/arm/completed";
            a.LogPath = "/opt/arm/logs";
        });
        var conductor = CreateConductor(options: options);
        await conductor.RunAsync("/dev/sr0");

        var job = _db.Jobs.Single();
        var config = job.Config;
        Assert.NotNull(config);
        Assert.Equal("/opt/arm/raw", config.RawPath);
        Assert.Equal("/opt/arm/completed", config.CompletedPath);
    }

    private sealed class MockIdentifyService(DiscType resultType = DiscType.Dvd, string? label = null) : IIdentifyService
    {
        public Task IdentifyAsync(Job job, CancellationToken ct = default)
        {
            job.DiscType = resultType;
            job.Label = label;
            if (resultType is DiscType.Dvd or DiscType.Bluray)
            {
                job.Title = "Test Movie";
                job.TitleAuto = "Test Movie";
                job.Year = "2024";
                job.VideoType = "movie";
                job.HasNiceTitle = true;
            }
            return Task.CompletedTask;
        }

        public Task EjectAsync(Job job, CancellationToken ct = default)
        {
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
