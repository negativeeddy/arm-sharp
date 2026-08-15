using System.Reflection;
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

/// <summary>
/// Simulates the job-977 failure mode end-to-end at the rip stage: a damaged disc
/// skips the real main feature (MSG:3015 navigation error) and MakeMKV saves a
/// salvaged 9s clip as the output for the selected TINFO index. Asserts that B3's
/// post-rip ffprobe duration verification fails the job at the rip stage instead of
/// letting the wrong file ship as Success.
/// </summary>
public sealed class RipVerificationIntegrationTests : IDisposable
{
    private readonly ArmDbContext _db;
    private readonly string _tmpRoot;
    private readonly IOptions<ArmSettings> _options;

    public RipVerificationIntegrationTests()
    {
        _db = TestHelpers.CreateDbContext();
        _tmpRoot = Path.Combine(Path.GetTempPath(), "arm-rip-verify", Guid.NewGuid().ToString());
        _options = TestHelpers.CreateOptions(a =>
        {
            a.RawPath = Path.Combine(_tmpRoot, "raw");
            a.TranscodePath = Path.Combine(_tmpRoot, "transcode");
            a.CompletedPath = Path.Combine(_tmpRoot, "completed");
            a.MinLength = 300;
            a.MaxLength = 99999;
        });
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_tmpRoot))
            Directory.Delete(_tmpRoot, recursive: true);
    }

    private static MethodInfo GetPrepareTranscodeInputPathAsync()
        => typeof(ArmRipperService).GetMethod("PrepareTranscodeInputPathAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
           ?? throw new InvalidOperationException("PrepareTranscodeInputPathAsync not found");

    private static MakeMkvRipResult Job977RipResult()
    {
        var result = new MakeMkvRipResult();
        result.Capture(new MakeMkvMessage(2003, 0, 3,
            "Error 'Scsi error - MEDIUM ERROR:UNRECOVERED READ ERROR' occurred while reading 'DVD' at offset '2381783040'",
            "Error '%1' occurred while reading '%2' at offset '%3'",
            ["Scsi error - MEDIUM ERROR:UNRECOVERED READ ERROR", "DVD", "2381783040"]));
        result.Capture(new MakeMkvMessage(3015, 0, 2,
            "Title #1 (1:49:15) was skipped due to navigation error",
            "Title #%1 (%2) was skipped due to navigation error",
            ["1", "1:49:15"]));
        result.Capture(new MakeMkvMessage(3028, 0, 3,
            "Title #2 was added (1 cell(s), 0:00:09)",
            "Title #%1 was added (%2 cell(s), %3)",
            ["2", "1", "0:00:09"]));
        return result;
    }

    private (ArmRipperService Service, Job Job, Mock<IMakeMkvService> MakeMkv, Mock<IFfmpegService> Ffmpeg, IRipRedirectService Redirect) CreateService(
        IRipRedirectService? redirectService = null,
        IReadOnlyList<Track>? tracks = null)
    {
        redirectService ??= new RipRedirectService();

        var job = TestHelpers.CreateTestJob(
            configure: j => j.DiscFingerprint = null,
            configureConfig: c => c.MainFeature = true);

        _db.Jobs.Add(job);
        _db.SaveChanges();

        var runner = new Mock<ICliProcessRunner>();
        runner.Setup(r => r.RunAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CliResult(0, "", "", false));

        var makeMkv = new Mock<IMakeMkvService>();
        var ffmpeg = new Mock<IFfmpegService>();

        tracks ??= new List<Track>
        {
            new()
            {
                JobId = job.Id,
                TrackNumber = "0",
                FileName = "title00.mkv",
                Length = 6547,                // 1:49:15 — the info-scan estimate
                FileSize = 4_000_000_000L,
                Chapters = 16,
                AspectRatio = "16:9",
                Fps = 23.976,
                Source = "MakeMKV",
                BaseName = job.Title
            }
        };

        makeMkv.Setup(m => m.GetTrackInfoWithCacheAsync(
                It.IsAny<Job>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => tracks.ToList());

        var service = new ArmRipperService(
            NullLoggerFactory.Instance,
            _db,
            makeMkv.Object,
            Mock.Of<IHandBrakeService>(),
            ffmpeg.Object,
            runner.Object,
            new NotificationService(NullLoggerFactory.Instance, _db, runner.Object, Mock.Of<IHttpClientFactory>(), []),
            _options,
            [],
            Mock.Of<IIdentifyService>(),
            Mock.Of<IDiscDbMappingService>(),
            Mock.Of<ITrackMapperService>(),
            redirectService);

        return (service, job, makeMkv, ffmpeg, redirectService);
    }

    private static async Task<string?> InvokeAsync(ArmRipperService service, Job job, string makeMkvOutPath)
    {
        var jobTitle = ArmRipperService.FixJobTitle(job);
        var task = (Task<string?>)GetPrepareTranscodeInputPathAsync()
            .Invoke(service, [job, jobTitle, makeMkvOutPath, CancellationToken.None])!;
        return await task;
    }

    [Fact]
    public async Task DamagedDisc_SavedWrongTitle_FailsJobAtRipStage()
    {
        var (service, job, makeMkv, ffmpeg, _) = CreateService();

        var ripResult = Job977RipResult();
        makeMkv.Setup(m => m.RipTrackAsync(
                It.IsAny<Job>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(),
                It.IsAny<IProgress<int>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ripResult);

        // The salvaged 9s clip: size is plausible (sparse file ~ expected size) so B2's
        // size gate passes, but ffprobe reports only 9 seconds — the B3 duration check.
        var makeMkvOutPath = Path.Combine(_options.Value.RawPath!, ArmRipperService.FixJobTitle(job));
        Directory.CreateDirectory(makeMkvOutPath);
        var outputFile = Path.Combine(makeMkvOutPath, "title_t00.mkv");
        using (var fs = new FileStream(outputFile, FileMode.CreateNew))
            fs.SetLength(4_000_000_000L);

        ffmpeg.Setup(f => f.ProbeDurationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(9.0);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => InvokeAsync(service, job, makeMkvOutPath));

        Assert.Equal(JobState.Failure, job.Status);
        Assert.Contains("Main feature rip verification failed", ex.Message);
        Assert.Contains("track 0", ex.Message);
        Assert.Equal(job.Errors, ex.Message);
    }

    [Fact]
    public async Task HealthyRip_DoesNotFailAtRipStage()
    {
        var (service, job, makeMkv, ffmpeg, _) = CreateService();

        makeMkv.Setup(m => m.RipTrackAsync(
                It.IsAny<Job>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(),
                It.IsAny<IProgress<int>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MakeMkvRipResult());

        var makeMkvOutPath = Path.Combine(_options.Value.RawPath!, ArmRipperService.FixJobTitle(job));
        Directory.CreateDirectory(makeMkvOutPath);
        var outputFile = Path.Combine(makeMkvOutPath, "title_t00.mkv");
        using (var fs = new FileStream(outputFile, FileMode.CreateNew))
            fs.SetLength(4_000_000_000L);

        ffmpeg.Setup(f => f.ProbeDurationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(6540.0);

        var result = await InvokeAsync(service, job, makeMkvOutPath);

        Assert.Equal(makeMkvOutPath, result);
        Assert.NotEqual(JobState.Failure, job.Status);
        Assert.Null(job.Errors);
    }

    [Fact]
    public async Task MainFeatureOverride_HonoredAtSelection_RipsChosenTrack()
    {
        var (service, job, makeMkv, ffmpeg, _) = CreateService(tracks: new List<Track>
        {
            new()
            {
                JobId = 1,
                TrackNumber = "0",
                FileName = "title00.mkv",
                Length = 9000,                 // longest → auto-selected main feature
                FileSize = 5_000_000_000L,
                Chapters = 30,
                AspectRatio = "16:9",
                Fps = 23.976,
                Source = "MakeMKV",
                BaseName = "Test Movie"
            },
            new()
            {
                JobId = 1,
                TrackNumber = "1",
                FileName = "title01.mkv",
                Length = 6000,
                FileSize = 3_000_000_000L,
                Chapters = 12,
                AspectRatio = "4:3",
                Fps = 23.976,
                Source = "MakeMKV",
                BaseName = "Test Movie"
            }
        });

        // The user chose track 1 even though track 0 is the longest.
        job.MainFeatureOverrideTrackNumber = "1";
        _db.SaveChanges();

        var makeMkvOutPath = Path.Combine(_options.Value.RawPath!, ArmRipperService.FixJobTitle(job));
        Directory.CreateDirectory(makeMkvOutPath);
        var outputFile = Path.Combine(makeMkvOutPath, "title_t01.mkv");
        using (var fs = new FileStream(outputFile, FileMode.CreateNew))
            fs.SetLength(3_000_000_000L);

        ffmpeg.Setup(f => f.ProbeDurationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(6000.0);

        var result = await InvokeAsync(service, job, makeMkvOutPath);

        Assert.Equal(makeMkvOutPath, result);
        makeMkv.Verify(m => m.RipTrackAsync(
                It.IsAny<Job>(), "1", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(),
                It.IsAny<IProgress<int>?>(), It.IsAny<CancellationToken>()),
            Times.Once);
        makeMkv.Verify(m => m.RipTrackAsync(
                It.IsAny<Job>(), "0", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(),
                It.IsAny<IProgress<int>?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task FingerprintOverride_HonoredAtSelection_RipsRememberedTrack()
    {
        var (service, job, makeMkv, ffmpeg, _) = CreateService(tracks: new List<Track>
        {
            new()
            {
                JobId = 1,
                TrackNumber = "0",
                FileName = "title00.mkv",
                Length = 9000,                 // longest → auto-selected main feature
                FileSize = 5_000_000_000L,
                Chapters = 30,
                AspectRatio = "16:9",
                Fps = 23.976,
                Source = "MakeMKV",
                BaseName = "Test Movie"
            },
            new()
            {
                JobId = 1,
                TrackNumber = "1",
                FileName = "title01.mkv",
                Length = 6000,
                FileSize = 3_000_000_000L,
                Chapters = 12,
                AspectRatio = "4:3",
                Fps = 23.976,
                Source = "MakeMKV",
                BaseName = "Test Movie"
            }
        });

        // A previous rip of this disc remembered track 1 as the main feature.
        job.DiscFingerprint = "TEST_FP";
        _db.DiscMetadata.Add(new DiscMetadata
        {
            Fingerprint = "TEST_FP",
            VolumeLabel = "TEST DISC",
            SectorCount = 0,
            DiscType = "DVD",
            CreatedAt = DateTime.UtcNow,
            LastUsedAt = DateTime.UtcNow,
            MainFeatureTrackNumber = "1"
        });
        _db.SaveChanges();

        var makeMkvOutPath = Path.Combine(_options.Value.RawPath!, ArmRipperService.FixJobTitle(job));
        Directory.CreateDirectory(makeMkvOutPath);
        var outputFile = Path.Combine(makeMkvOutPath, "title_t01.mkv");
        using (var fs = new FileStream(outputFile, FileMode.CreateNew))
            fs.SetLength(3_000_000_000L);

        ffmpeg.Setup(f => f.ProbeDurationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(6000.0);

        var result = await InvokeAsync(service, job, makeMkvOutPath);

        Assert.Equal(makeMkvOutPath, result);
        makeMkv.Verify(m => m.RipTrackAsync(
                It.IsAny<Job>(), "1", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(),
                It.IsAny<IProgress<int>?>(), It.IsAny<CancellationToken>()),
            Times.Once);
        makeMkv.Verify(m => m.RipTrackAsync(
                It.IsAny<Job>(), "0", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(),
                It.IsAny<IProgress<int>?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task MidRipRedirect_CancelsActiveRip_AndReripsChosenTrack()
    {
        var redirect = new RipRedirectService();
        var (service, job, makeMkv, ffmpeg, _) = CreateService(redirect, tracks: new List<Track>
        {
            new()
            {
                JobId = 1,
                TrackNumber = "0",
                FileName = "title00.mkv",
                Length = 9000,                 // longest → auto-selected main feature
                FileSize = 5_000_000_000L,
                Chapters = 30,
                AspectRatio = "16:9",
                Fps = 23.976,
                Source = "MakeMKV",
                BaseName = "Test Movie"
            },
            new()
            {
                JobId = 1,
                TrackNumber = "1",
                FileName = "title01.mkv",
                Length = 6000,
                FileSize = 3_000_000_000L,
                Chapters = 12,
                AspectRatio = "4:3",
                Fps = 23.976,
                Source = "MakeMKV",
                BaseName = "Test Movie"
            }
        });

        var makeMkvOutPath = Path.Combine(_options.Value.RawPath!, ArmRipperService.FixJobTitle(job));

        // Track 0 is ripped first; mid-rip the user redirects to track 1, which
        // cancels the active rip (OCE) and leaves a partial output file behind.
        var track0Ripped = false;
        makeMkv.Setup(m => m.RipTrackAsync(
                It.IsAny<Job>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(),
                It.IsAny<IProgress<int>?>(), It.IsAny<CancellationToken>()))
            .Returns(async (Job j, string track, string outPath, string args, int minLen, IProgress<int>? prog, CancellationToken token) =>
            {
                Directory.CreateDirectory(outPath);
                if (!track0Ripped)
                {
                    track0Ripped = true;
                    using var partial = new FileStream(Path.Combine(outPath, "title_t00.mkv"), FileMode.Create);
                    partial.SetLength(500_000L);

                    // The user picks track 1 while the rip is in progress.
                    j.MainFeatureOverrideTrackNumber = "1";
                    _db.SaveChanges();
                    redirect.RequestRedirect(j.Id);

                    throw new OperationCanceledException();
                }

                using var output = new FileStream(Path.Combine(outPath, "title_t01.mkv"), FileMode.Create);
                output.SetLength(3_000_000_000L);
                return new MakeMkvRipResult();
            });

        ffmpeg.Setup(f => f.ProbeDurationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(6000.0);

        var result = await InvokeAsync(service, job, makeMkvOutPath);

        Assert.Equal(makeMkvOutPath, result);
        makeMkv.Verify(m => m.RipTrackAsync(
                It.IsAny<Job>(), "0", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(),
                It.IsAny<IProgress<int>?>(), It.IsAny<CancellationToken>()),
            Times.Once);
        makeMkv.Verify(m => m.RipTrackAsync(
                It.IsAny<Job>(), "1", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(),
                It.IsAny<IProgress<int>?>(), It.IsAny<CancellationToken>()),
            Times.Once);

        // The partial rip of track 0 was cleaned up; only the re-ripped file remains.
        Assert.False(File.Exists(Path.Combine(makeMkvOutPath, "title_t00.mkv")));
        Assert.True(File.Exists(Path.Combine(makeMkvOutPath, "title_t01.mkv")));

        // The redirect persisted the choice and track 1 became the main feature.
        Assert.Equal("1", job.MainFeatureOverrideTrackNumber);
        var savedTrack = _db.Tracks.First(t => t.JobId == job.Id && t.TrackNumber == "1");
        Assert.True(savedTrack.MainFeature);
    }
}
