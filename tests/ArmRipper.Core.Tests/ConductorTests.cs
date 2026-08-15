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
        ICliProcessRunner? runner = null,
        ISettingsService? settingsService = null)
    {
        runner ??= CreateMockRunner().Object;
        var musicBrainzService = musicBrainz ?? new Mock<IMusicBrainzService>().Object;
        options ??= CreateTestOptions();
        return new Conductor(
            NullLoggerFactory.Instance,
            _db,
            runner,
            options,
            settingsService ?? TestHelpers.CreateSettingsService(options),
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
        var tmpDir = Path.Combine(Path.GetTempPath(), "arm-test", Guid.NewGuid().ToString());
        var options = TestHelpers.CreateOptions(a =>
        {
            a.RawPath = Path.Combine(tmpDir, "raw");
            a.TranscodePath = Path.Combine(tmpDir, "transcode");
            a.CompletedPath = Path.Combine(tmpDir, "completed");
            a.LogPath = Path.Combine(tmpDir, "logs");
        });
        var conductor = CreateConductor(options: options);
        await conductor.RunAsync("/dev/sr0");

        var job = _db.Jobs.Single();
        var config = job.Config;
        Assert.NotNull(config);
        Assert.Equal(Path.Combine(tmpDir, "raw"), config.RawPath);
        Assert.Equal(Path.Combine(tmpDir, "completed"), config.CompletedPath);
    }

    [Fact]
    public async Task RunAsync_WhenDriveOverridesMainFeatureToAll_OverridesGlobalSetting()
    {
        _db.SystemDrives.Add(new SystemDrive
        {
            SerialId = "SR-TEST-ALL",
            Mount = "/dev/sr0",
            Model = "Test Drive",
            MainFeature = false // "All" — rip every title
        });
        await _db.SaveChangesAsync();

        var conductor = CreateConductor();
        await conductor.RunAsync("/dev/sr0");

        var job = _db.Jobs.Single();
        Assert.NotNull(job.Config);
        Assert.False(job.Config.MainFeature);
    }

    [Fact]
    public async Task RunAsync_WhenDriveOverridesMainFeatureToMain_OverridesGlobalSetting()
    {
        var options = TestHelpers.CreateOptions(a =>
        {
            a.MainFeature = false; // global says "All"
            a.RawPath = Path.Combine(Path.GetTempPath(), "arm-test", Guid.NewGuid().ToString(), "raw");
            a.TranscodePath = Path.Combine(Path.GetTempPath(), "arm-test", Guid.NewGuid().ToString(), "transcode");
            a.CompletedPath = Path.Combine(Path.GetTempPath(), "arm-test", Guid.NewGuid().ToString(), "completed");
            a.LogPath = Path.Combine(Path.GetTempPath(), "arm-test", Guid.NewGuid().ToString(), "logs");
        });
        _db.SystemDrives.Add(new SystemDrive
        {
            SerialId = "SR-TEST-MAIN",
            Mount = "/dev/sr0",
            Model = "Test Drive",
            MainFeature = true // "Main" — rip main feature only
        });
        await _db.SaveChangesAsync();

        var conductor = CreateConductor(options: options);
        await conductor.RunAsync("/dev/sr0");

        var job = _db.Jobs.Single();
        Assert.NotNull(job.Config);
        Assert.True(job.Config.MainFeature);
    }

    [Fact]
    public async Task RunAsync_WhenDriveHasNoOverride_UsesGlobalSetting()
    {
        _db.SystemDrives.Add(new SystemDrive
        {
            SerialId = "SR-TEST-DEFAULT",
            Mount = "/dev/sr0",
            Model = "Test Drive",
            MainFeature = null
        });
        await _db.SaveChangesAsync();

        var conductor = CreateConductor();
        await conductor.RunAsync("/dev/sr0");

        var job = _db.Jobs.Single();
        Assert.NotNull(job.Config);
        Assert.True(job.Config.MainFeature); // global default is true
    }

    [Theory]
    [InlineData(JobState.Failure)]
    [InlineData(JobState.Success)]
    public async Task RunResumeAsync_WithNonResumableStatus_AbortsBeforeIdentification(JobState status)
    {
        var identifyMock = new Mock<IIdentifyService>();
        identifyMock.Setup(i => i.IdentifyAsync(It.IsAny<Job>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var conductor = CreateConductor(identify: identifyMock.Object);

        var job = TestHelpers.CreateTestJob(j => j.Status = status);
        _db.Jobs.Add(job);
        await _db.SaveChangesAsync();

        var exitCode = await conductor.RunResumeAsync(job.Id);

        // Non-resumable status must abort with failure code, never touching identification
        Assert.Equal(1, exitCode);
        identifyMock.Verify(
            i => i.IdentifyAsync(It.IsAny<Job>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // Job status must not be advanced by the aborted run
        var dbJob = await _db.Jobs.FindAsync(job.Id);
        Assert.NotNull(dbJob);
        Assert.Equal(status, dbJob.Status);
    }

    [Theory]
    [InlineData(JobState.Stopping)]
    [InlineData(JobState.Cancelled)]
    public async Task RunResumeAsync_WithResumableStatus_ProceedsToIdentification(JobState status)
    {
        var identifyMock = new Mock<IIdentifyService>();
        identifyMock.Setup(i => i.IdentifyAsync(It.IsAny<Job>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var conductor = CreateConductor(identify: identifyMock.Object);

        var job = TestHelpers.CreateTestJob(j => j.Status = status);
        _db.Jobs.Add(job);
        await _db.SaveChangesAsync();

        await conductor.RunResumeAsync(job.Id);

        // Resumable statuses (Stopping/Cancelled) must not early-return at the guard —
        // execution must proceed all the way into identification.
        identifyMock.Verify(
            i => i.IdentifyAsync(It.IsAny<Job>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
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
                job.VideoType = VideoContentType.Movie;
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
