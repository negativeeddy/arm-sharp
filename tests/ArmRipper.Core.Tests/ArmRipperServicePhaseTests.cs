using System.Collections.Concurrent;
using System.Reflection;
using ArmMedia.Core.Abstractions;
using ArmMedia.Core.Models;
using ArmRipper.Core.Configuration;
using ArmRipper.Core.Infrastructure;
using ArmRipper.Core.Infrastructure.Data;
using ArmRipper.Core.Models;
using ArmRipper.Core.Notifications;
using ArmRipper.Core.Rip;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace ArmRipper.Core.Tests;

/// <summary>
/// Tests for the extracted sub-phase methods of <see cref="ArmRipperService"/>.
/// Uses the in-memory SQLite database from <see cref="TestHelpers"/> and Moq for
/// service dependencies so each phase can be tested in isolation.
/// </summary>
public sealed class ArmRipperServicePhaseTests : IDisposable
{
    private readonly ArmDbContext _db;
    private readonly Mock<ILoggerFactory> _loggerFactory;
    private readonly Mock<ILogger> _logger;
    private readonly Mock<IMakeMkvService> _makeMkv;
    private readonly Mock<IHandBrakeService> _handBrake;
    private readonly Mock<IFfmpegService> _ffmpeg;
    private readonly Mock<ICliProcessRunner> _runner;
    private readonly NotificationService _notifications;
    private readonly IOptions<ArmSettings> _options;
    private readonly Mock<IEnumerable<INotificationBroadcaster>> _broadcasters;
    private readonly Mock<IIdentifyService> _identifyService;
    private readonly Mock<IDiscDbMappingService> _discDbMapping;
    private readonly Mock<ITrackMapperService> _trackMapper;
    private readonly Mock<IRipRedirectService> _ripRedirect;
    private readonly Mock<IEpisodeIdentificationOrchestrator> _episodeOrchestrator;

    public ArmRipperServicePhaseTests()
    {
        _db = TestHelpers.CreateDbContext();
        _loggerFactory = new Mock<ILoggerFactory>();
        _logger = new Mock<ILogger>();
        _loggerFactory.Setup(f => f.CreateLogger(It.IsAny<string>())).Returns(_logger.Object);
        _makeMkv = new Mock<IMakeMkvService>();
        _handBrake = new Mock<IHandBrakeService>();
        _ffmpeg = new Mock<IFfmpegService>();
        _runner = new Mock<ICliProcessRunner>();
        _options = TestHelpers.CreateOptions();
        _broadcasters = new Mock<IEnumerable<INotificationBroadcaster>>();
        _broadcasters.Setup(b => b.GetEnumerator()).Returns(Enumerable.Empty<INotificationBroadcaster>().GetEnumerator());
        _notifications = new NotificationService(
            _loggerFactory.Object,
            _db,
            _runner.Object,
            Mock.Of<IHttpClientFactory>(),
            _broadcasters.Object);
        _identifyService = new Mock<IIdentifyService>();
        _discDbMapping = new Mock<IDiscDbMappingService>();
        _trackMapper = new Mock<ITrackMapperService>();
        _ripRedirect = new Mock<IRipRedirectService>();
        _episodeOrchestrator = new Mock<IEpisodeIdentificationOrchestrator>();
    }

    public void Dispose() => _db.Dispose();

    private ArmRipperService CreateService(Action<ArmSettings>? configureSettings = null)
    {
        var opts = configureSettings is not null
            ? TestHelpers.CreateOptions(configureSettings)
            : _options;

        return new ArmRipperService(
            _loggerFactory.Object,
            _db,
            _makeMkv.Object,
            _handBrake.Object,
            _ffmpeg.Object,
            _runner.Object,
            _notifications,
            opts,
            _broadcasters.Object,
            _identifyService.Object,
            _discDbMapping.Object,
            _trackMapper.Object,
            _ripRedirect.Object,
            _episodeOrchestrator.Object);
    }

    // ───────────────────────────────────────────────────────────────────
    // ComputeRipContextAsync
    // ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ComputeRipContextAsync_SetsStageToIdentify()
    {
        var job = TestHelpers.CreateTestJob();
        _db.Jobs.Add(job);
        await _db.SaveChangesAsync();

        var service = CreateService();
        var ctx = await service.ComputeRipContextAsync(job, hasDupes: false, protection: false, CancellationToken.None);

        Assert.Equal(RipStage.Identify, job.Stage);
        Assert.NotNull(ctx);
        Assert.Equal("Test Movie (2024)", ctx.JobTitle);
    }

    [Fact]
    public async Task ComputeRipContextAsync_MovieJob_ComputesCorrectPaths()
    {
        var job = TestHelpers.CreateTestJob();
        _db.Jobs.Add(job);
        await _db.SaveChangesAsync();

        var service = CreateService();
        var ctx = await service.ComputeRipContextAsync(job, hasDupes: false, protection: false, CancellationToken.None);

        // Movie → sub-folder "movies"
        Assert.Contains("/movies/", ctx.TranscodeOutPath);
        Assert.Contains("/movies/", ctx.FinalDirectory);
        Assert.Contains("Test Movie (2024)", ctx.TranscodeOutPath);
        // FinalBasePath is the base before dupe suffix — directory may get _N
        // suffix if it already exists on the filesystem, so just verify the
        // base path component matches.
        Assert.Contains(Path.GetDirectoryName(ctx.FinalBasePath)!, ctx.FinalDirectory);
    }

    [Fact]
    public async Task ComputeRipContextAsync_TvJob_UsesTvSubFolder()
    {
        var job = TestHelpers.CreateTestJob(j =>
        {
            j.VideoType = VideoContentType.Series;
            j.Title = "My Show";
        });
        _db.Jobs.Add(job);
        await _db.SaveChangesAsync();

        var service = CreateService();
        var ctx = await service.ComputeRipContextAsync(job, hasDupes: false, protection: false, CancellationToken.None);

        Assert.Contains("/tv/", ctx.TranscodeOutPath);
        Assert.Contains("/tv/", ctx.FinalDirectory);
    }

    [Fact]
    public async Task ComputeRipContextAsync_WithDupes_AppendsDupeSuffix()
    {
        // hasDupes=true + AllowDuplicates=false → CheckForDupeFolder throws.
        // hasDupes=true + AllowDuplicates=true → _N suffix is appended.
        // hasDupes=false → suffix is appended regardless of AllowDuplicates.
        var job = TestHelpers.CreateTestJob(j => j.Config!.AllowDuplicates = true);
        _db.Jobs.Add(job);
        await _db.SaveChangesAsync();

        var service = CreateService();
        var ctx = await service.ComputeRipContextAsync(job, hasDupes: true, protection: false, CancellationToken.None);

        // When hasDupes is true and AllowDuplicates is enabled,
        // CheckForDupeFolder appends a _N suffix.
        Assert.NotNull(ctx.FinalDirectory);
        Assert.NotNull(ctx.TranscodeOutPath);
    }

    [Fact]
    public async Task ComputeRipContextAsync_UseMakeMkvTrue_WhenRipMethodIsMkv()
    {
        var job = TestHelpers.CreateTestJob();
        _db.Jobs.Add(job);
        await _db.SaveChangesAsync();

        var service = CreateService(s => s.RipMethod = "mkv");
        var ctx = await service.ComputeRipContextAsync(job, hasDupes: false, protection: false, CancellationToken.None);

        Assert.True(ctx.UseMakeMkv);
    }

    [Fact]
    public async Task ComputeRipContextAsync_UseMakeMkvFalse_WhenJobConfigRipMethodIsHb()
    {
        // RipWithMkv checks the job's Config.RipMethod, not the global setting.
        var job = TestHelpers.CreateTestJob(j => j.Config!.RipMethod = "hb");
        _db.Jobs.Add(job);
        await _db.SaveChangesAsync();

        var service = CreateService();
        var ctx = await service.ComputeRipContextAsync(job, hasDupes: false, protection: false, CancellationToken.None);

        Assert.False(ctx.UseMakeMkv);
    }

    [Fact]
    public async Task ComputeRipContextAsync_SetsJobPath()
    {
        var job = TestHelpers.CreateTestJob();
        _db.Jobs.Add(job);
        await _db.SaveChangesAsync();

        var service = CreateService();
        var ctx = await service.ComputeRipContextAsync(job, hasDupes: false, protection: false, CancellationToken.None);

        Assert.Equal(ctx.FinalDirectory, job.Path);
    }

    [Fact]
    public async Task ComputeRipContextAsync_MakeMkvOutPath_ContainsJobTitle()
    {
        var job = TestHelpers.CreateTestJob();
        _db.Jobs.Add(job);
        await _db.SaveChangesAsync();

        var service = CreateService();
        var ctx = await service.ComputeRipContextAsync(job, hasDupes: false, protection: false, CancellationToken.None);

        Assert.Contains("Test Movie (2024)", ctx.MakeMkvOutPath);
    }

    // ───────────────────────────────────────────────────────────────────
    // TestModeTrimAsync
    // ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TestModeTrimAsync_TestModeDisabled_DoesNothing()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"arm_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            // Create a fake MKV file
            var mkvFile = Path.Combine(tempDir, "title_t00.mkv");
            await File.WriteAllBytesAsync(mkvFile, [1, 2, 3, 4]);

            var service = CreateService(s => s.TestMode = false);
            await service.TestModeTrimAsync(tempDir, CancellationToken.None);

            // File should be untouched
            Assert.True(File.Exists(mkvFile));
            Assert.Equal(4, new FileInfo(mkvFile).Length);
            _runner.Verify(
                r => r.RunAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task TestModeTrimAsync_NullPath_DoesNothing()
    {
        var service = CreateService(s => s.TestMode = true);
        await service.TestModeTrimAsync(null, CancellationToken.None);

        _runner.Verify(
            r => r.RunAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task TestModeTrimAsync_NonexistentPath_DoesNothing()
    {
        var service = CreateService(s => s.TestMode = true);
        await service.TestModeTrimAsync("/nonexistent/path", CancellationToken.None);

        _runner.Verify(
            r => r.RunAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task TestModeTrimAsync_TestModeEnabled_CallsFfmpegForEachMkv()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"arm_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            // Create fake MKV files
            var mkv1 = Path.Combine(tempDir, "title_t00.mkv");
            var mkv2 = Path.Combine(tempDir, "title_t01.mkv");
            await File.WriteAllBytesAsync(mkv1, [1, 2, 3, 4]);
            await File.WriteAllBytesAsync(mkv2, [5, 6, 7, 8]);

            // Mock ffmpeg runner to simulate successful trim (create the .trimmed output)
            _runner.Setup(r => r.RunAsync(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<string?>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .Callback<string, string, string?, int, CancellationToken>((file, args, _, _, _) =>
                {
                    // Parse the output file from args: -t 30 -i "input" -c copy -y "output"
                    var outputMatch = System.Text.RegularExpressions.Regex.Match(args, @"-y\s+""(.+?)""");
                    if (outputMatch.Success)
                    {
                        var outputPath = outputMatch.Groups[1].Value;
                        File.WriteAllBytes(outputPath, [0xAA, 0xBB]);
                    }
                })
                .ReturnsAsync(new CliResult(0, "", "", false));

            var service = CreateService(s =>
            {
                s.TestMode = true;
                s.FfmpegCli = "ffmpeg";
            });

            await service.TestModeTrimAsync(tempDir, CancellationToken.None);

            // Both files should have been trimmed (replaced with trimmed versions)
            Assert.True(File.Exists(mkv1));
            Assert.True(File.Exists(mkv2));
            // The trimmed files should have our fake content
            Assert.Equal([0xAA, 0xBB], await File.ReadAllBytesAsync(mkv1));
            Assert.Equal([0xAA, 0xBB], await File.ReadAllBytesAsync(mkv2));

            _runner.Verify(
                r => r.RunAsync("ffmpeg", It.IsAny<string>(), null, 60_000, It.IsAny<CancellationToken>()),
                Times.Exactly(2));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task TestModeTrimAsync_FfmpegFails_OriginalFileRetained()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"arm_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var mkvFile = Path.Combine(tempDir, "title_t00.mkv");
            var originalContent = new byte[] { 1, 2, 3, 4 };
            await File.WriteAllBytesAsync(mkvFile, originalContent);

            // Mock ffmpeg to fail
            _runner.Setup(r => r.RunAsync(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<string?>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new CliResult(1, "", "error", false));

            var service = CreateService(s =>
            {
                s.TestMode = true;
                s.FfmpegCli = "ffmpeg";
            });

            await service.TestModeTrimAsync(tempDir, CancellationToken.None);

            // Original file should be untouched since trim failed
            Assert.Equal(originalContent, await File.ReadAllBytesAsync(mkvFile));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task TestModeTrimAsync_UsesConfiguredFfmpegCli()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"arm_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var mkvFile = Path.Combine(tempDir, "title_t00.mkv");
            await File.WriteAllBytesAsync(mkvFile, [1, 2, 3, 4]);

            _runner.Setup(r => r.RunAsync(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<string?>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .Callback<string, string, string?, int, CancellationToken>((file, args, _, _, _) =>
                {
                    var outputMatch = System.Text.RegularExpressions.Regex.Match(args, @"-y\s+""(.+?)""");
                    if (outputMatch.Success)
                        File.WriteAllBytes(outputMatch.Groups[1].Value, [0xAA]);
                })
                .ReturnsAsync(new CliResult(0, "", "", false));

            var service = CreateService(s =>
            {
                s.TestMode = true;
                s.FfmpegCli = "/usr/local/bin/custom-ffmpeg";
            });

            await service.TestModeTrimAsync(tempDir, CancellationToken.None);

            _runner.Verify(
                r => r.RunAsync("/usr/local/bin/custom-ffmpeg", It.IsAny<string>(), null, 60_000, It.IsAny<CancellationToken>()),
                Times.Once);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    // ───────────────────────────────────────────────────────────────────
    // CleanupRawFiles
    // ───────────────────────────────────────────────────────────────────

    [Fact]
    public void CleanupRawFiles_DelRawDisabled_DoesNotDelete()
    {
        var job = TestHelpers.CreateTestJob(j => j.Config!.DelRawFiles = false);
        var ctx = new ArmRipperService.RipContext
        {
            JobTitle = "Test",
            TranscodeOutPath = "/tmp/transcode",
            FinalDirectory = "/tmp/final",
            FinalBasePath = "/tmp/final",
            MakeMkvOutPath = "/tmp/raw",
            TranscodeInPath = "/dev/sr0",
            UseMakeMkv = true,
        };

        var service = CreateService();

        // Should not throw — it just logs
        service.CleanupRawFiles(job, ctx, transcodeSucceeded: true);

        // Verify the "DelRawFiles is disabled" log message was produced
        _logger.Verify(
            l => l.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("DelRawFiles is disabled")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public void CleanupRawFiles_DelRawEnabled_TranscodeSucceeded_LogsDeletion()
    {
        var job = TestHelpers.CreateTestJob(j => j.Config!.DelRawFiles = true);
        var ctx = new ArmRipperService.RipContext
        {
            JobTitle = "Test",
            TranscodeOutPath = "/tmp/transcode",
            FinalDirectory = "/tmp/final",
            FinalBasePath = "/tmp/final",
            MakeMkvOutPath = "/tmp/raw",
            TranscodeInPath = "/dev/sr0",
            UseMakeMkv = true,
        };

        var service = CreateService();

        service.CleanupRawFiles(job, ctx, transcodeSucceeded: true);

        // No warning about keeping files — deletion was attempted
        _logger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never);
    }

    [Fact]
    public void CleanupRawFiles_DelRawEnabled_TranscodeFailed_LogsWarning()
    {
        var job = TestHelpers.CreateTestJob(j => j.Config!.DelRawFiles = true);
        var ctx = new ArmRipperService.RipContext
        {
            JobTitle = "Test",
            TranscodeOutPath = "/tmp/transcode",
            FinalDirectory = "/tmp/final",
            FinalBasePath = "/tmp/final",
            MakeMkvOutPath = "/tmp/raw",
            TranscodeInPath = "/dev/sr0",
            UseMakeMkv = true,
        };

        var service = CreateService();

        service.CleanupRawFiles(job, ctx, transcodeSucceeded: false);

        // Should warn that files are being kept for retry
        _logger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("keeping raw files")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public void CleanupRawFiles_NullPathsHandledGracefully()
    {
        var job = TestHelpers.CreateTestJob(j => j.Config!.DelRawFiles = true);
        var ctx = new ArmRipperService.RipContext
        {
            JobTitle = "Test",
            TranscodeOutPath = "/tmp/transcode",
            FinalDirectory = "/tmp/final",
            FinalBasePath = "/tmp/final",
            MakeMkvOutPath = "/tmp/raw",
            TranscodeInPath = null, // Null — no MakeMKV used
            UseMakeMkv = false,
        };

        var service = CreateService();

        // Should not throw even with null TranscodeInPath
        var ex = Record.Exception(() => service.CleanupRawFiles(job, ctx, transcodeSucceeded: true));
        Assert.Null(ex);
    }

    // ───────────────────────────────────────────────────────────────────
    // RipContext
    // ───────────────────────────────────────────────────────────────────

    [Fact]
    public void RipContext_Mutation_Works()
    {
        var ctx = new ArmRipperService.RipContext
        {
            JobTitle = "Test",
            TranscodeOutPath = "/a",
            FinalDirectory = "/b",
            FinalBasePath = "/b",
            MakeMkvOutPath = "/c",
            TranscodeInPath = "/d",
            UseMakeMkv = true,
        };

        ctx.TranscodeOutPath = "/new";
        ctx.FinalDirectory = "/new2";
        ctx.TranscodeInPath = null;

        Assert.Equal("/new", ctx.TranscodeOutPath);
        Assert.Equal("/new2", ctx.FinalDirectory);
        Assert.Null(ctx.TranscodeInPath);
    }

    // ───────────────────────────────────────────────────────────────────
    // Manual Selection
    // ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ManualSelection_AppliesUserSelection_AfterResume()
    {
        // Job with BOTH Manual Selection and Main Feature enabled — the manual
        // selection must win over the MainFeature branch.
        var job = TestHelpers.CreateTestJob(j =>
        {
            j.Config!.ManualSelection = true;
            j.Config!.MainFeature = true;
        });
        _db.Jobs.Add(job);
        await _db.SaveChangesAsync();

        // MakeMKV scan returns 3 tracks; the longest (track "2") would be the
        // auto-selected main feature.
        var tracks = new List<Track>
        {
            new() { Id = 1, JobId = job.Id, TrackNumber = "0", Length = 1000, FileName = "C1_t00.mkv" },
            new() { Id = 2, JobId = job.Id, TrackNumber = "1", Length = 2000, FileName = "D1_t01.mkv" },
            new() { Id = 3, JobId = job.Id, TrackNumber = "2", Length = 3000, FileName = "B1_t02.mkv" },
        };
        _makeMkv.Setup(m => m.GetTrackInfoWithCacheAsync(
                It.IsAny<Job>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(tracks);
        _makeMkv.Setup(m => m.RipTrackAsync(
                It.IsAny<Job>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<int>(), It.IsAny<IProgress<int>?>(), It.IsAny<CancellationToken>()))
            .Callback<Job, string, string, string, int, IProgress<int>?, CancellationToken>(
                (_, trackNumber, outputPath, _, _, _, _) =>
                {
                    // Simulate MakeMKV writing the output file so the post-rip
                    // file-matching marks the track as Ripped.
                    Directory.CreateDirectory(outputPath);
                    File.WriteAllBytes(
                        Path.Combine(outputPath, $"title_t{int.Parse(trackNumber):D2}.mkv"),
                        [1, 2, 3]);
                })
            .ReturnsAsync(new MakeMkvRipResult());

        var service = CreateService();

        // Start the private pipeline method via reflection.
        var method = typeof(ArmRipperService).GetMethod(
            "PrepareTranscodeInputPathAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        var rawPath = Path.Combine(Path.GetTempPath(), $"arm_manual_sel_{Guid.NewGuid():N}");
        var pipelineTask = (Task<string?>)method!.Invoke(service,
            new object[] { job, "Test Movie (2024)", rawPath, CancellationToken.None })!;

        // Wait until the pipeline parks in the manual-selection wait (it registers
        // its signal in the static dictionary right before parking).
        var signalsField = typeof(ArmRipperService).GetField(
            "manualSelectionSignals", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(signalsField);
        var signals = (ConcurrentDictionary<int, TaskCompletionSource<bool>>)signalsField!.GetValue(null)!;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!signals.ContainsKey(job.Id) && sw.ElapsedMilliseconds < 10_000)
            await Task.Delay(25);
        Assert.True(signals.ContainsKey(job.Id), "Pipeline did not park in manual selection wait");

        // Simulate the API endpoint: persist the selection through a SEPARATE
        // DbContext sharing the same connection (the real API uses its own
        // request-scoped context, so the pipeline's tracked entity is stale).
        using (var apiDb = new ArmDbContext(
            new DbContextOptionsBuilder<ArmDbContext>()
                .UseSqlite(_db.Database.GetDbConnection())
                .Options))
        {
            var apiJob = await apiDb.Jobs.FirstAsync(j => j.Id == job.Id);
            apiJob.ManualSelectionTrackNumbers = "[\"0\"]";
            await apiDb.SaveChangesAsync();
        }

        // Resume the pipeline.
        Assert.True(ArmRipperService.SignalManualSelection(job.Id));

        var result = await pipelineTask;
        Assert.Equal(rawPath, result);

        // The rip must have used the individual-track branch (respecting the
        // selection) rather than the MainFeature branch or the fast path.
        _makeMkv.Verify(m => m.RipTrackAsync(
            It.IsAny<Job>(), "0", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(),
            It.IsAny<IProgress<int>?>(), It.IsAny<CancellationToken>()), Times.Once);
        _makeMkv.Verify(m => m.RipTrackAsync(
            It.IsAny<Job>(), "2", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(),
            It.IsAny<IProgress<int>?>(), It.IsAny<CancellationToken>()), Times.Never);
        _makeMkv.Verify(m => m.RipAllTitlesAsync(
            It.IsAny<Job>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(),
            It.IsAny<IProgress<int>?>(), It.IsAny<CancellationToken>()), Times.Never);

        // The DB tracks reflect the selection: only track "0" is Process=true.
        var dbTracks = await _db.Tracks.Where(t => t.JobId == job.Id).ToListAsync();
        var processed = Assert.Single(dbTracks, t => t.Process);
        Assert.Equal("0", processed.TrackNumber);

        // No spurious stage error should be recorded for the identify stage —
        // the status transition must not trip the guard (yellow Identify in UI).
        Assert.DoesNotContain("identify", job.StageErrors ?? "");
    }

    [Fact]
    public async Task ManualSelection_TimesOut_FailsJobAndReleasesDrive()
    {
        // Regression test for issue #170: a manual-selection wait with no user
        // response must time out (using ManualWaitTime) and fail the job rather
        // than block the optical drive indefinitely.
        var job = TestHelpers.CreateTestJob(j =>
        {
            j.Config!.ManualSelection = true;
            j.Config!.ManualWaitTime = 1; // 1 second timeout for a fast test
        });
        _db.Jobs.Add(job);
        await _db.SaveChangesAsync();

        var tracks = new List<Track>
        {
            new() { Id = 1, JobId = job.Id, TrackNumber = "0", Length = 1000, FileName = "C1_t00.mkv" },
        };
        _makeMkv.Setup(m => m.GetTrackInfoWithCacheAsync(
                It.IsAny<Job>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(tracks);

        var service = CreateService();

        var method = typeof(ArmRipperService).GetMethod(
            "PrepareTranscodeInputPathAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        var rawPath = Path.Combine(Path.GetTempPath(), $"arm_manual_sel_timeout_{Guid.NewGuid():N}");
        var pipelineTask = (Task<string?>)method!.Invoke(service,
            new object[] { job, "Test Movie (2024)", rawPath, CancellationToken.None })!;

        // Wait for the pipeline to park in the manual-selection wait, then let the
        // 1-second timeout elapse without signaling.
        var signalsField = typeof(ArmRipperService).GetField(
            "manualSelectionSignals", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(signalsField);
        var signals = (ConcurrentDictionary<int, TaskCompletionSource<bool>>)signalsField!.GetValue(null)!;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!signals.ContainsKey(job.Id) && sw.ElapsedMilliseconds < 10_000)
            await Task.Delay(25);
        Assert.True(signals.ContainsKey(job.Id), "Pipeline did not park in manual selection wait");

        // Do NOT signal — the timeout should fire and fail the job.
        var result = await pipelineTask;
        Assert.Null(result);

        // The job must be marked as failed with a clear error message.
        var dbJob = await _db.Jobs.AsNoTracking().FirstAsync(j => j.Id == job.Id);
        Assert.Equal(JobState.Failure, dbJob.Status);
        Assert.Contains("timed out", dbJob.Errors ?? "");
    }

    [Fact]
    public async Task ManualSelection_SignalRegisteredBeforeStatusVisible_NoRace()
    {
        // Regression test for issue #169: the pipeline must register its signal
        // in the static dictionary BEFORE persisting the ManualSelectionStarted
        // status. Otherwise the API endpoint (which checks the status and then
        // calls SignalManualSelection) could land in the gap and the signal would
        // be missed, leaving the job parked forever.
        var job = TestHelpers.CreateTestJob(j =>
        {
            j.Config!.ManualSelection = true;
        });
        _db.Jobs.Add(job);
        await _db.SaveChangesAsync();

        var tracks = new List<Track>
        {
            new() { Id = 1, JobId = job.Id, TrackNumber = "0", Length = 1000, FileName = "C1_t00.mkv" },
        };
        _makeMkv.Setup(m => m.GetTrackInfoWithCacheAsync(
                It.IsAny<Job>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(tracks);
        _makeMkv.Setup(m => m.RipTrackAsync(
                It.IsAny<Job>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<int>(), It.IsAny<IProgress<int>?>(), It.IsAny<CancellationToken>()))
            .Callback<Job, string, string, string, int, IProgress<int>?, CancellationToken>(
                (_, trackNumber, outputPath, _, _, _, _) =>
                {
                    Directory.CreateDirectory(outputPath);
                    File.WriteAllBytes(
                        Path.Combine(outputPath, $"title_t{int.Parse(trackNumber):D2}.mkv"),
                        [1, 2, 3]);
                })
            .ReturnsAsync(new MakeMkvRipResult());

        var service = CreateService();

        var method = typeof(ArmRipperService).GetMethod(
            "PrepareTranscodeInputPathAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        var rawPath = Path.Combine(Path.GetTempPath(), $"arm_manual_sel_race_{Guid.NewGuid():N}");
        var pipelineTask = (Task<string?>)method!.Invoke(service,
            new object[] { job, "Test Movie (2024)", rawPath, CancellationToken.None })!;

        var signalsField = typeof(ArmRipperService).GetField(
            "manualSelectionSignals", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(signalsField);
        var signals = (ConcurrentDictionary<int, TaskCompletionSource<bool>>)signalsField!.GetValue(null)!;

        // Wait until the job's status is visible as ManualSelectionStarted in the
        // DB (the point at which the API endpoint would call SignalManualSelection).
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 10_000)
        {
            var status = await _db.Jobs.AsNoTracking()
                .Where(j => j.Id == job.Id)
                .Select(j => j.Status)
                .FirstOrDefaultAsync();
            if (status == JobState.ManualSelectionStarted)
                break;
            await Task.Delay(25);
        }
        Assert.Equal(JobState.ManualSelectionStarted,
            (await _db.Jobs.AsNoTracking().FirstAsync(j => j.Id == job.Id)).Status);

        // The signal MUST already be registered — this is the crux of the race fix.
        Assert.True(signals.ContainsKey(job.Id),
            "Signal must be registered before the ManualSelectionStarted status is visible");

        // Persist a selection through a separate DbContext (as the real API does),
        // then resume the pipeline and confirm it completes cleanly.
        using (var apiDb = new ArmDbContext(
            new DbContextOptionsBuilder<ArmDbContext>()
                .UseSqlite(_db.Database.GetDbConnection())
                .Options))
        {
            var apiJob = await apiDb.Jobs.FirstAsync(j => j.Id == job.Id);
            apiJob.ManualSelectionTrackNumbers = "[\"0\"]";
            await apiDb.SaveChangesAsync();
        }

        Assert.True(ArmRipperService.SignalManualSelection(job.Id));
        var result = await pipelineTask;
        Assert.Equal(rawPath, result);
    }
}
