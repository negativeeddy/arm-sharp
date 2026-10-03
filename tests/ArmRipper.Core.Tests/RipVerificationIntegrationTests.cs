using System.Reflection;
using ArmRipper.Core.Configuration;
using ArmRipper.Core.Infrastructure;
using ArmRipper.Core.Infrastructure.Data;
using ArmRipper.Core.Models;
using ArmRipper.Core.Notifications;
using ArmRipper.Core.Rip;
using Microsoft.EntityFrameworkCore;
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

    // ── Rip output → track binding ──────────────────────────────

    [Theory]
    // MakeMKV's TINFO Filename and the output file share the 0-based TID.
    [InlineData("Movie_t134.mkv", "134")]
    [InlineData("Movie_t00.mkv", "0")]
    [InlineData("Movie_t07.mkv", "7")]
    [InlineData("Movie_t03.mkv", "3")]
    public void MatchRipOutputToTrack_OutputNameIndexMatchesTrackNumber(
        string fileName, string expectedTid)
    {
        // Regression: the old matcher searched for "t{TID}" as a substring, so
        // "t03" claimed "t030" and a rip could be bound to a neighbouring title.
        var tracks = new List<Track>
        {
            new Track { TrackNumber = "0" },
            new Track { TrackNumber = "3" },
            new Track { TrackNumber = "7" },
            new Track { TrackNumber = "30" },
            new Track { TrackNumber = "134" },
        };

        var match = ArmRipperService.MatchRipOutputToTrack(tracks, fileName);

        Assert.NotNull(match);
        Assert.Equal(expectedTid, match!.TrackNumber);
    }

    [Fact]
    public void MatchRipOutputToTrack_ExactInfoScanFilename_Wins()
    {
        var tracks = new List<Track>
        {
            new Track { TrackNumber = "9", FileName = "Some Title_t08.mkv" },
        };

        var match = ArmRipperService.MatchRipOutputToTrack(tracks, "Some Title_t08.mkv");

        Assert.NotNull(match);
        Assert.Equal("9", match!.TrackNumber);
    }

    [Fact]
    public void MatchRipOutputToTrack_PrefixCollision_DoesNotMatchWrongTrack()
    {
        // "t03" must not claim "t030" (regression: substring Contains() match).
        var tracks = new List<Track>
        {
            new Track { TrackNumber = "3" },
            new Track { TrackNumber = "30" },
        };

        var match = ArmRipperService.MatchRipOutputToTrack(tracks, "Movie_t03.mkv");

        Assert.NotNull(match);
        Assert.Equal("3", match!.TrackNumber);
    }

    [Fact]
    public void MatchRipOutputToTrack_UnknownOutputIndex_ReturnsNull()
    {
        var tracks = new List<Track> { new Track { TrackNumber = "135" } };

        Assert.Null(ArmRipperService.MatchRipOutputToTrack(tracks, "Movie_t999.mkv"));
    }

    [Theory]
    [InlineData("Movie_t134.mkv", 134)]
    [InlineData("Movie_t00.mkv", 0)]
    [InlineData("Movie.mkv", null)]
    [InlineData("", null)]
    public void RipOutputIndex_ParsesZeroBasedSuffix(string fileName, int? expected)
        => Assert.Equal(expected, ArmRipperService.RipOutputIndex(fileName));

    [Fact]
    public void RipOutputIndex_NonNumericSuffix_ReturnsNull()
    {
        Assert.Null(ArmRipperService.RipOutputIndex("The_titles_treasure.mkv"));
    }

    [Fact]
    public void PurgeStaleRipOutput_RemovesLeftoversFromPreviousJob()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"arm-purge-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "Movie_t132.mkv"), "stale sting");
            File.WriteAllText(Path.Combine(dir, "Movie_t134.mkv"), "stale feature");
            File.WriteAllText(Path.Combine(dir, "notes.txt"), "keep me");

            var removed = ArmRipperService.PurgeStaleRipOutput(dir);

            Assert.Equal(2, removed);
            Assert.Empty(Directory.EnumerateFiles(dir, "*.mkv"));
            Assert.True(File.Exists(Path.Combine(dir, "notes.txt")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void PurgeStaleRipOutput_MissingDirectory_ReturnsZero()
        => Assert.Equal(0, ArmRipperService.PurgeStaleRipOutput(
            Path.Combine(Path.GetTempPath(), $"arm-purge-missing-{Guid.NewGuid():N}")));

    // ── Drive-open fault (MSG 5010) ────────────────────────────

    [Fact]
    public void RipResult_DiscOpenError_IsNotReportedAsDiscReadError()
    {
        var result = new MakeMkvRipResult();
        result.Capture(new MakeMkvMessage((int)MessageId.RipDiscOpenError, 0, 0,
            "Failed to open disc", "Failed to open disc", []));

        Assert.True(result.HadDiscOpenError);
        Assert.False(result.HadReadError);
    }

    [Fact]
    public void MakeMkvRipResult_Merge_PropagatesDiscOpenError()
    {
        var a = new MakeMkvRipResult();
        var b = new MakeMkvRipResult();
        b.Capture(new MakeMkvMessage((int)MessageId.RipDiscOpenError, 0, 0,
            "Failed to open disc", "Failed to open disc", []));

        a.Merge(b);

        Assert.True(a.HadDiscOpenError);
    }

    /// <summary>
    /// Builds a <see cref="MakeMkvRipResult"/> that reports the given number of
    /// titles saved (MSG 3028), simulating a successful MakeMKV rip. A bare
    /// <c>new MakeMkvRipResult()</c> reports 0 titles saved, which the rip loop
    /// now treats as a failure.
    /// </summary>
    private static MakeMkvRipResult SuccessfulRipResult(int titlesSaved = 1)
    {
        var result = new MakeMkvRipResult();
        for (var i = 0; i < titlesSaved; i++)
            result.Capture(new MakeMkvMessage((int)MessageId.TitleAdded, 0, 1, "Title added", "", []));
        return result;
    }

    private (ArmRipperService Service, Job Job, Mock<IMakeMkvService> MakeMkv, Mock<IFfmpegService> Ffmpeg, IRipRedirectService Redirect) CreateService(
        IRipRedirectService? redirectService = null,
        IReadOnlyList<Track>? tracks = null,
        Action<ConfigSnapshot>? configureConfig = null)
    {
        redirectService ??= new RipRedirectService();

        var job = TestHelpers.CreateTestJob(
            configure: j => j.DiscFingerprint = null,
            configureConfig: c =>
            {
                c.MainFeature = true;
                configureConfig?.Invoke(c);
            });

        _db.Jobs.Add(job);
        _db.SaveChanges();

        var runner = new Mock<ICliProcessRunner>();
        runner.Setup(r => r.RunAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CliResult(0, "", "", false));

        var makeMkv = new Mock<IMakeMkvService>();
        var ffmpeg = new Mock<IFfmpegService>();

        tracks ??= new List<Track>
        {
            new()
            {
                JobId = job.Id,
                TrackNumber = "1",
                FileName = "title_t00.mkv",
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

    /// <summary>
    /// Configures <c>RipTrackAsync</c> to write the given output file, as MakeMKV
    /// does. Output must be produced by the mock rip rather than pre-seeded in the
    /// directory: the rip purges stale files before it starts, so pre-created files
    /// would be deleted and the track would never be marked as ripped.
    /// </summary>
    private static void SetupRipWritingFile(Mock<IMakeMkvService> makeMkv, string outputPath, string fileName, long size)
    {
        makeMkv.Setup(m => m.RipTrackAsync(
                It.IsAny<Job>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(),
                It.IsAny<IProgress<int>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Job _, string _tn, string outPath, string _a, int _ml,
                IProgress<int>? _p, CancellationToken _ct) =>
            {
                Directory.CreateDirectory(outPath);
                using var fs = new FileStream(Path.Combine(outPath, fileName), FileMode.Create, FileAccess.Write);
                fs.SetLength(size);
                return SuccessfulRipResult();
            });
    }

    private static async Task<string?> InvokeAsync(ArmRipperService service, Job job, string makeMkvOutPath, CancellationToken ct = default)
    {
        var jobTitle = ArmRipperService.FixJobTitle(job);
        var task = (Task<string?>)GetPrepareTranscodeInputPathAsync()
            .Invoke(service, [job, jobTitle, makeMkvOutPath, ct])!;
        return await task;
    }

    [Fact]
    public async Task DamagedDisc_SavedWrongTitle_FailsJobAtRipStage()
    {
        var (service, job, makeMkv, ffmpeg, _) = CreateService();

        // The salvaged 9s clip: size is plausible (sparse file ~ expected size) so B2's
        // size gate passes, but ffprobe reports only 9 seconds — the B3 duration check.
        // The mock must write the file, since the rip purges stale output first.
        var makeMkvOutPath = Path.Combine(_options.Value.RawPath!, ArmRipperService.FixJobTitle(job));
        makeMkv.Setup(m => m.RipTrackAsync(
                It.IsAny<Job>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(),
                It.IsAny<IProgress<int>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Job _, string _tn, string outPath, string _a, int _ml,
                IProgress<int>? _p, CancellationToken _ct) =>
            {
                Directory.CreateDirectory(outPath);
                using var fs = new FileStream(Path.Combine(outPath, "title_t00.mkv"),
                    FileMode.Create, FileAccess.Write);
                fs.SetLength(4_000_000_000L);
                return Job977RipResult();
            });

        ffmpeg.Setup(f => f.ProbeDurationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(9.0);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => InvokeAsync(service, job, makeMkvOutPath));

        Assert.Equal(JobState.Failure, job.Status);
        Assert.Contains("Main feature rip verification failed", ex.Message);
        Assert.Contains("track 1", ex.Message);
        Assert.Equal(job.Errors, ex.Message);
    }

    [Fact]
    public async Task HealthyRip_DoesNotFailAtRipStage()
    {
        var (service, job, makeMkv, ffmpeg, _) = CreateService();

        var makeMkvOutPath = Path.Combine(_options.Value.RawPath!, ArmRipperService.FixJobTitle(job));
        SetupRipWritingFile(makeMkv, makeMkvOutPath, "title_t00.mkv", 4_000_000_000L);

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
                TrackNumber = "1",
                FileName = "title_t00.mkv",
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
                TrackNumber = "2",
                FileName = "title_t01.mkv",
                Length = 6000,
                FileSize = 3_000_000_000L,
                Chapters = 12,
                AspectRatio = "4:3",
                Fps = 23.976,
                Source = "MakeMKV",
                BaseName = "Test Movie"
            }
        });

        // The user chose track 2 even though track 1 is the longest.
        job.MainFeatureOverrideTrackNumber = "2";
        _db.SaveChanges();

        var makeMkvOutPath = Path.Combine(_options.Value.RawPath!, ArmRipperService.FixJobTitle(job));
        SetupRipWritingFile(makeMkv, makeMkvOutPath, "title_t01.mkv", 3_000_000_000L);

        ffmpeg.Setup(f => f.ProbeDurationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(6000.0);

        var result = await InvokeAsync(service, job, makeMkvOutPath);

        Assert.Equal(makeMkvOutPath, result);
        makeMkv.Verify(m => m.RipTrackAsync(
                It.IsAny<Job>(), "2", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(),
                It.IsAny<IProgress<int>?>(), It.IsAny<CancellationToken>()),
            Times.Once);
        makeMkv.Verify(m => m.RipTrackAsync(
                It.IsAny<Job>(), "1", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(),
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
                TrackNumber = "1",
                FileName = "title_t00.mkv",
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
                TrackNumber = "2",
                FileName = "title_t01.mkv",
                Length = 6000,
                FileSize = 3_000_000_000L,
                Chapters = 12,
                AspectRatio = "4:3",
                Fps = 23.976,
                Source = "MakeMKV",
                BaseName = "Test Movie"
            }
        });

        // A previous rip of this disc remembered track 2 as the main feature.
        job.DiscFingerprint = "TEST_FP";
        _db.DiscMetadata.Add(new DiscMetadata
        {
            Fingerprint = "TEST_FP",
            VolumeLabel = "TEST DISC",
            SectorCount = 0,
            DiscType = "DVD",
            CreatedAt = DateTime.UtcNow,
            LastUsedAt = DateTime.UtcNow,
            MainFeatureTrackNumber = "2"
        });
        _db.SaveChanges();

        var makeMkvOutPath = Path.Combine(_options.Value.RawPath!, ArmRipperService.FixJobTitle(job));
        SetupRipWritingFile(makeMkv, makeMkvOutPath, "title_t01.mkv", 3_000_000_000L);

        ffmpeg.Setup(f => f.ProbeDurationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(6000.0);

        var result = await InvokeAsync(service, job, makeMkvOutPath);

        Assert.Equal(makeMkvOutPath, result);
        makeMkv.Verify(m => m.RipTrackAsync(
                It.IsAny<Job>(), "2", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(),
                It.IsAny<IProgress<int>?>(), It.IsAny<CancellationToken>()),
            Times.Once);
        makeMkv.Verify(m => m.RipTrackAsync(
                It.IsAny<Job>(), "1", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(),
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
                TrackNumber = "1",
                FileName = "title_t00.mkv",
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
                TrackNumber = "2",
                FileName = "title_t01.mkv",
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

        // Track 1 is ripped first; mid-rip the user redirects to track 2, which
        // cancels the active rip (OCE) and leaves a partial output file behind.
        var track1Ripped = false;
        makeMkv.Setup(m => m.RipTrackAsync(
                It.IsAny<Job>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(),
                It.IsAny<IProgress<int>?>(), It.IsAny<CancellationToken>()))
            .Returns(async (Job j, string track, string outPath, string args, int minLen, IProgress<int>? prog, CancellationToken token) =>
            {
                Directory.CreateDirectory(outPath);
                if (!track1Ripped)
                {
                    track1Ripped = true;
                    using var partial = new FileStream(Path.Combine(outPath, "title_t00.mkv"), FileMode.Create);
                    partial.SetLength(500_000L);

                    // The user picks track 2 while the rip is in progress.
                    j.MainFeatureOverrideTrackNumber = "2";
                    _db.SaveChanges();
                    redirect.RequestRedirect(j.Id);

                    throw new OperationCanceledException();
                }

                using var output = new FileStream(Path.Combine(outPath, "title_t01.mkv"), FileMode.Create);
                output.SetLength(3_000_000_000L);
                return SuccessfulRipResult();
            });

        ffmpeg.Setup(f => f.ProbeDurationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(6000.0);

        var result = await InvokeAsync(service, job, makeMkvOutPath);

        Assert.Equal(makeMkvOutPath, result);
        makeMkv.Verify(m => m.RipTrackAsync(
                It.IsAny<Job>(), "1", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(),
                It.IsAny<IProgress<int>?>(), It.IsAny<CancellationToken>()),
            Times.Once);
        makeMkv.Verify(m => m.RipTrackAsync(
                It.IsAny<Job>(), "2", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(),
                It.IsAny<IProgress<int>?>(), It.IsAny<CancellationToken>()),
            Times.Once);

        // The partial rip of track 1 was cleaned up; only the re-ripped file remains.
        Assert.False(File.Exists(Path.Combine(makeMkvOutPath, "title_t00.mkv")));
        Assert.True(File.Exists(Path.Combine(makeMkvOutPath, "title_t01.mkv")));

        // The redirect persisted the choice and track 2 became the main feature.
        Assert.Equal("2", job.MainFeatureOverrideTrackNumber);
        var savedTrack = _db.Tracks.First(t => t.JobId == job.Id && t.TrackNumber == "2");
        Assert.True(savedTrack.MainFeature);
    }

    [Fact]
    public async Task UserCancelMidRip_AbortsTrackLoop_AndPropagatesCancellation()
    {
        // Regression: the individual-track rip loop used to swallow
        // OperationCanceledException in its catch (Exception) block, logging a user
        // cancel as a track failure and continuing to rip the remaining tracks.
        // MainFeature off + DiscDb-promoted tracks (EpisodeTitle) force the
        // individual-track branch (the all-titles fast path requires no EpisodeTitle).
        var (service, job, makeMkv, ffmpeg, _) = CreateService(
            configureConfig: c => c.MainFeature = false,
            tracks: new List<Track>
            {
                new()
                {
                    JobId = 1,
                    TrackNumber = "1",
                    FileName = "title_t00.mkv",
                    Length = 6547,
                    FileSize = 4_000_000_000L,
                    Chapters = 16,
                    AspectRatio = "16:9",
                    Fps = 23.976,
                    Source = "MakeMKV",
                    BaseName = "Test Movie",
                    EpisodeTitle = "Episode 1",
                    Process = true
                },
                new()
                {
                    JobId = 1,
                    TrackNumber = "2",
                    FileName = "title_t01.mkv",
                    Length = 6000,
                    FileSize = 3_000_000_000L,
                    Chapters = 12,
                    AspectRatio = "4:3",
                    Fps = 23.976,
                    Source = "MakeMKV",
                    BaseName = "Test Movie",
                    EpisodeTitle = "Episode 2",
                    Process = true
                }
            });

        var makeMkvOutPath = Path.Combine(_options.Value.RawPath!, ArmRipperService.FixJobTitle(job));

        // The user cancels the job while the first rip is in progress: the mock
        // cancels the pipeline token and throws OCE, exactly like RipTrackAsync
        // does when the underlying process is cancelled.
        using var cts = new CancellationTokenSource();
        makeMkv.Setup(m => m.RipTrackAsync(
                It.IsAny<Job>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(),
                It.IsAny<IProgress<int>?>(), It.IsAny<CancellationToken>()))
            .Returns(async (Job j, string track, string outPath, string args, int minLen, IProgress<int>? prog, CancellationToken token) =>
            {
                cts.Cancel();
                throw new OperationCanceledException();
            });

        // The cancellation must propagate out of the rip stage (so the conductor
        // can transition the job to Stopping) rather than being logged as a track
        // failure and swallowed.
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            InvokeAsync(service, job, makeMkvOutPath, cts.Token));

        // The loop aborted after the first track — no further rip attempts.
        makeMkv.Verify(m => m.RipTrackAsync(
                It.IsAny<Job>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(),
                It.IsAny<IProgress<int>?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task StaleTrackedEntity_OverrideWrittenBySeparateScope_FallsBackToAsNoTracking()
    {
        // Simulates the production scenario: the pipeline holds a stale tracked Job
        // entity (MainFeatureOverrideTrackNumber is null). A separate DbContext scope
        // (e.g. the redirect API endpoint) writes the override. The pipeline then
        // falls back to an AsNoTracking DB read and picks up the override.
        var (service, job, makeMkv, ffmpeg, _) = CreateService(tracks: new List<Track>
        {
            new()
            {
                JobId = 1,
                TrackNumber = "1",
                FileName = "title_t00.mkv",
                Length = 9000,
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
                TrackNumber = "2",
                FileName = "title_t01.mkv",
                Length = 6000,
                FileSize = 3_000_000_000L,
                Chapters = 12,
                AspectRatio = "4:3",
                Fps = 23.976,
                Source = "MakeMKV",
                BaseName = "Test Movie"
            }
        });

        // The pipeline's tracked entity has no override — it was loaded before
        // the user clicked "Redirect" in the UI.
        Assert.Null(job.MainFeatureOverrideTrackNumber);

        // Simulate the production scenario: a separate DbContext scope (the API
        // controller) writes the override. We use ExecuteSqlRaw on the same
        // connection to bypass EF Core change tracking, then detach the tracked
        // entity so the pipeline sees a stale copy (MainFeatureOverrideTrackNumber
        // is still null in memory).
        await _db.Database.ExecuteSqlAsync(
            $"UPDATE Jobs SET MainFeatureOverrideTrackNumber = '2' WHERE Id = {job.Id}");
        _db.Entry(job).State = EntityState.Detached;

        var makeMkvOutPath = Path.Combine(_options.Value.RawPath!, ArmRipperService.FixJobTitle(job));
        SetupRipWritingFile(makeMkv, makeMkvOutPath, "title_t01.mkv", 3_000_000_000L);

        ffmpeg.Setup(f => f.ProbeDurationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(6000.0);

        var result = await InvokeAsync(service, job, makeMkvOutPath);

        Assert.Equal(makeMkvOutPath, result);

        // Track 2 was ripped despite the pipeline's tracked entity being stale.
        makeMkv.Verify(m => m.RipTrackAsync(
                It.IsAny<Job>(), "2", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(),
                It.IsAny<IProgress<int>?>(), It.IsAny<CancellationToken>()),
            Times.Once);
        makeMkv.Verify(m => m.RipTrackAsync(
                It.IsAny<Job>(), "1", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(),
                It.IsAny<IProgress<int>?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(12)]
    public async Task TitleScanCompleted_SetsNoOfTitlesOnJob(int trackCount)
    {
        var tracks = Enumerable.Range(1, trackCount)
            .Select(i => new Track
            {
                JobId = 1,
                TrackNumber = i.ToString(),
                FileName = $"title_t{i - 1:D2}.mkv",
                Length = 6000 + i * 100,
                FileSize = 1_000_000_000L + i * 100_000_000L,
                Chapters = 10 + i,
                AspectRatio = "16:9",
                Fps = 23.976,
                Source = "MakeMKV",
                BaseName = "Test Movie"
            })
            .ToList();

        var (service, job, makeMkv, ffmpeg, _) = CreateService(tracks: tracks);

        var makeMkvOutPath = Path.Combine(_options.Value.RawPath!, ArmRipperService.FixJobTitle(job));

        // The mock rip writes one output per requested track (as MakeMKV does); the
        // service purges stale output first, so files cannot be pre-seeded.
        makeMkv.Setup(m => m.RipTrackAsync(
                It.IsAny<Job>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(),
                It.IsAny<IProgress<int>?>(), It.IsAny<CancellationToken>()))
            .Returns((Job _j, string trackNumber, string outPath, string _a, int _ml,
                IProgress<int>? _p, CancellationToken _ct) =>
            {
                var track = tracks.First(t => t.TrackNumber == trackNumber);
                Directory.CreateDirectory(outPath);
                using var fs = new FileStream(Path.Combine(outPath, track.FileName!),
                    FileMode.Create, FileAccess.Write);
                fs.SetLength(track.FileSize ?? 1_000_000_000L);
                return Task.FromResult(SuccessfulRipResult());
            });

        ffmpeg.Setup(f => f.ProbeDurationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string file, CancellationToken _) =>
            {
                // Return the scan-length for whichever file MakeMKV "ripped".
                var match = tracks.FirstOrDefault(t => t.FileName != null && file.EndsWith(t.FileName));
                return (double)(match?.Length ?? 6000);
            });

        await InvokeAsync(service, job, makeMkvOutPath);

        Assert.Equal(trackCount, job.NoOfTitles);

        // Also verify the value was persisted to the database.
        var dbJob = await _db.Jobs.FindAsync(job.Id);
        Assert.Equal(trackCount, dbJob!.NoOfTitles);
    }
}
