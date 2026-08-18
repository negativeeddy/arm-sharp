using ArmRipper.Core.Infrastructure;
using ArmRipper.Core.Infrastructure.Data;
using ArmRipper.Core.Models;
using ArmRipper.Core.Rip;
using Microsoft.EntityFrameworkCore;
using DriveInfo = ArmRipper.Core.Rip.DriveInfo;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ArmRipper.Core.Tests;

public sealed class MakeMkvServiceTests : IDisposable
{
    private readonly ArmDbContext _db;
    private readonly Mock<ICliProcessRunner> _runnerMock;
    private readonly MakeMkvService _service;

    public MakeMkvServiceTests()
    {
        _db = TestHelpers.CreateDbContext();
        _runnerMock = new Mock<ICliProcessRunner>();

        _runnerMock
            .Setup(r => r.RunAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CliResult(0, "", "", false));

        _runnerMock
            .Setup(r => r.RunStreamingAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(AsyncEnumerable.Empty<string>());

        _service = new MakeMkvService(
            _runnerMock.Object,
            NullLoggerFactory.Instance,
            TestHelpers.CreateOptions(),
            _db,
            Mock.Of<IHttpClientFactory>());
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    private static async IAsyncEnumerable<string> ToAsyncStream(params string[] lines)
    {
        foreach (var line in lines)
        {
            yield return line;
            await Task.CompletedTask;
        }
    }

    private static async IAsyncEnumerable<(string? Line, bool IsStdErr, int? ExitCode)> ToStreamingAllAsync(params string[] lines)
    {
        foreach (var line in lines)
        {
            yield return (line, false, null);
            await Task.CompletedTask;
        }
    }

    private static async IAsyncEnumerable<(string? Line, bool IsStdErr, int? ExitCode)> ThrowingStreamingAllAsync()
    {
        yield return (null, false, null);
        await Task.CompletedTask;
        throw new OperationCanceledException();
    }



    // ── ParseLine tests ──────────────────────────────────────────

    [Fact]
    public void ParseLine_InvalidLine_ReturnsNull()
    {
        Assert.Null(_service.ParseLine("garbage without colon"));
    }

    [Fact]
    public void ParseLine_UnknownType_ReturnsNull()
    {
        Assert.Null(_service.ParseLine("UNKNOWN:some data"));
    }

    [Fact]
    public void ParseLine_Msg_ReturnsParsedMessage()
    {
        var result = _service.ParseLine("MSG:1005,0,1,\"MakeMKV v1.17.8\",\"\",");

        Assert.NotNull(result);
        Assert.Equal(MakeMkvOutputType.Msg, result.Type);
        var msg = Assert.IsType<MakeMkvMessage>(result.Data);
        Assert.Equal(1005, msg.Code);
        Assert.Equal(0, msg.Flags);
        Assert.Equal(1, msg.Count);
        Assert.Contains("MakeMKV", msg.Message);
    }

    [Fact]
    public void ParseLine_Msg_WithParams_ParsesAllFields()
    {
        var result = _service.ParseLine("MSG:3025,0,2,\"Title #%1 was skipped\",\"Title #%1 was skipped\",\"1\"");

        Assert.NotNull(result);
        var msg = Assert.IsType<MakeMkvMessage>(result.Data);
        Assert.Equal(3025, msg.Code);
        Assert.Equal(2, msg.Count);
        Assert.Equal("Title #%1 was skipped", msg.Message);
        Assert.Equal("1", msg.Params[0]);
    }

    [Fact]
    public void ParseLine_Drv_ReturnsDriveInfo()
    {
        var result = _service.ParseLine("DRV:0,2,999,12,\"BD-RE BU40N\",\"\",\"/dev/sr0\"");

        Assert.NotNull(result);
        Assert.Equal(MakeMkvOutputType.Drv, result.Type);
        var drv = Assert.IsType<DriveInfo>(result.Data);
        Assert.Equal(0, drv.Index);
        Assert.True(drv.Loaded);
        Assert.True(drv.Attached);
        Assert.Equal("BD-RE BU40N", drv.Info);
    }

    [Fact]
    public void ParseLine_Drv_NotAttached_SetsAttachedFalse()
    {
        var result = _service.ParseLine("DRV:0,256,999,0,\"\",\"\",\"\"");

        Assert.NotNull(result);
        var drv = Assert.IsType<DriveInfo>(result.Data);
        Assert.False(drv.Attached);
    }

    [Fact]
    public void ParseLine_Drv_OpenDrive_SetsIsOpen()
    {
        var result = _service.ParseLine("DRV:0,1,999,0,\"BD-RE BU40N\",\"\",\"/dev/sr0\"");

        Assert.NotNull(result);
        var drv = Assert.IsType<DriveInfo>(result.Data);
        Assert.True(drv.IsOpen);
    }

    [Fact]
    public void ParseLine_TCount_ReturnsTitles()
    {
        var result = _service.ParseLine("TCOUNT:5");

        Assert.NotNull(result);
        Assert.Equal(MakeMkvOutputType.TCount, result.Type);
        var titles = Assert.IsType<Titles>(result.Data);
        Assert.Equal(5, titles.Count);
    }

    [Fact]
    public void ParseLine_CInfo_ReturnsCInfo()
    {
        var result = _service.ParseLine("CINFO:1,6201,\"Video\"");

        Assert.NotNull(result);
        Assert.Equal(MakeMkvOutputType.CinFo, result.Type);
        var ci = Assert.IsType<CinFo>(result.Data);
        Assert.Equal(1, ci.Id);
        Assert.Equal(6201, ci.Code);
        Assert.Equal("Video", ci.Value);
    }

    [Fact]
    public void ParseLine_TInfo_ReturnsTInfo()
    {
        var result = _service.ParseLine("TINFO:0,9,0,\"01:23:45\"");

        Assert.NotNull(result);
        Assert.Equal(MakeMkvOutputType.TInfo, result.Type);
        var ti = Assert.IsType<TInfo>(result.Data);
        Assert.Equal(0, ti.Tid);
        Assert.Equal(9, ti.Id);
        Assert.Equal("01:23:45", ti.Value);
    }

    [Fact]
    public void ParseLine_TInfo_Filename_ContainsQuotedValue()
    {
        var result = _service.ParseLine("TINFO:0,27,0,\"\"\"title00.mkv\"\"\"");

        Assert.NotNull(result);
        var ti = Assert.IsType<TInfo>(result.Data);
        Assert.Equal(27, ti.Id);
        Assert.Equal("title00.mkv", ti.Value);
    }

    [Fact]
    public void ParseLine_SInfo_ReturnsSInfo()
    {
        var result = _service.ParseLine("SINFO:0,0,1,6201,\"Video\"");

        Assert.NotNull(result);
        Assert.Equal(MakeMkvOutputType.SinFo, result.Type);
        var si = Assert.IsType<SinFo>(result.Data);
        Assert.Equal(0, si.Tid);
        Assert.Equal(0, si.Sid);
        Assert.Equal(1, si.Id);
        Assert.Equal(6201, si.Code);
        Assert.Equal("Video", si.Value);
    }

    [Fact]
    public void ParseLine_SInfo_LanguageCode_ParsesCorrectly()
    {
        var result = _service.ParseLine("SINFO:0,0,2,0,\"eng\"");

        Assert.NotNull(result);
        var si = Assert.IsType<SinFo>(result.Data);
        Assert.Equal(2, si.Id);
        Assert.Equal("eng", si.Value);
    }

    [Fact]
    public void ParseLine_PrgV_ReturnsPrgV()
    {
        var result = _service.ParseLine("PRGV:1,5,45,100");

        Assert.NotNull(result);
        Assert.Equal(MakeMkvOutputType.PrgV, result.Type);
        var pv = Assert.IsType<PrgV>(result.Data);
        Assert.Equal(1, pv.CurrentTitle);
        Assert.Equal(5, pv.TotalTitles);
        Assert.Equal(45, pv.CurrentProgress);
        Assert.Equal(100, pv.TotalProgress);
    }

    [Fact]
    public void ParseLine_PrgC_ReturnsPrgC()
    {
        var result = _service.ParseLine("PRGC:500,1000");

        Assert.NotNull(result);
        Assert.Equal(MakeMkvOutputType.PrgC, result.Type);
        var pc = Assert.IsType<PrgC>(result.Data);
        Assert.Equal(500, pc.CurrentProgress);
        Assert.Equal(1000, pc.TotalProgress);
    }

    [Fact]
    public void ParseLine_PrgT_ReturnsPrgT()
    {
        var result = _service.ParseLine("PRGT:100");

        Assert.NotNull(result);
        Assert.Equal(MakeMkvOutputType.PrgT, result.Type);
        var pt = Assert.IsType<PrgT>(result.Data);
        Assert.Equal(100, pt.TotalProgress);
    }

    // ── GetTrackInfoAsync tests ──────────────────────────────────

    [Fact]
    public async Task GetTrackInfoAsync_TimedOut_ReturnsEmptyList()
    {
        _runnerMock
            .Setup(r => r.RunStreamingAllAsync(
                "makemkvcon",
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Returns(ThrowingStreamingAllAsync());

        var job = TestHelpers.CreateTestJob();
        var tracks = await _service.GetTrackInfoAsync(job, "base");

        Assert.Empty(tracks);
    }

    [Fact]
    public async Task GetTrackInfoAsync_ParsesMultipleTracks()
    {
        var output = """
            TCOUNT:2
            TINFO:0,9,0,"01:30:00"
            TINFO:0,27,0,"title00.mkv"
            TINFO:0,11,0,"5000000000"
            TINFO:0,8,0,"16"
            SINFO:0,0,1,6201,"Video"
            SINFO:0,0,2,0,"eng"
            SINFO:0,0,20,0,"16:9"
            SINFO:0,0,21,0,"23.976 fps"
            TINFO:1,9,0,"00:45:00"
            TINFO:1,27,0,"title01.mkv"
            TINFO:1,11,0,"2000000000"
            TINFO:1,8,0,"8"
            SINFO:1,0,1,6201,"Video"
            SINFO:1,0,2,0,"fra"
            SINFO:1,0,20,0,"4:3"
            SINFO:1,0,21,0,"25 fps"
            """;

        _runnerMock
            .Setup(r => r.RunStreamingAllAsync(
                "makemkvcon",
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Returns(ToStreamingAllAsync(output.Split('\n')));

        var job = TestHelpers.CreateTestJob();
        var tracks = await _service.GetTrackInfoAsync(job, "base");

        Assert.Equal(2, tracks.Count);

        var t0 = tracks[0];
        Assert.Equal("0", t0.TrackNumber);
        Assert.Equal(5400, t0.Length);
        Assert.Equal("16:9", t0.AspectRatio);
        Assert.Equal(23.976, t0.Fps);
        Assert.Equal(16, t0.Chapters);
        Assert.Equal(5000000000L, t0.FileSize);
        Assert.Equal("MakeMKV", t0.Source);

        var t1 = tracks[1];
        Assert.Equal("1", t1.TrackNumber);
        Assert.Equal(2700, t1.Length);
        Assert.Equal("4:3", t1.AspectRatio);
        Assert.Equal(25, t1.Fps);
        Assert.Equal(8, t1.Chapters);
        Assert.Equal(2000000000L, t1.FileSize);
    }

    [Fact]
    public async Task GetTrackInfoAsync_WithNoOutput_ReturnsEmptyList()
    {
        _runnerMock
            .Setup(r => r.RunStreamingAllAsync(
                "makemkvcon",
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Returns(ToStreamingAllAsync());

        var job = TestHelpers.CreateTestJob();
        var tracks = await _service.GetTrackInfoAsync(job, "base");

        Assert.Empty(tracks);
    }

    [Fact]
    public async Task GetTrackInfoAsync_PersistsDiscTrackCache()
    {
        var output = """
            TCOUNT:1
            TINFO:0,9,0,"01:30:00"
            TINFO:0,27,0,"title00.mkv"
            TINFO:0,11,0,"5000000000"
            TINFO:0,8,0,"16"
            SINFO:0,0,1,6201,"Video"
            SINFO:0,0,2,0,"eng"
            """;

        _runnerMock
            .Setup(r => r.RunStreamingAllAsync(
                "makemkvcon",
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Returns(ToStreamingAllAsync(output.Split('\n')));

        var job = TestHelpers.CreateTestJob(j => j.DiscFingerprint = "VOL::12345");
        var tracks = await _service.GetTrackInfoAsync(job, "base");

        Assert.Single(tracks);
        var cached = _db.DiscMetadata.FirstOrDefault(d => d.Fingerprint == "VOL::12345");
        Assert.NotNull(cached);
        Assert.Single(cached.Tracks);
        Assert.Equal("0", cached.Tracks.ElementAt(0).TrackNumber);
    }

    // ── GetTrackInfoWithCacheAsync tests ─────────────────────────

    [Fact]
    public async Task GetTrackInfoWithCacheAsync_CacheHit_ReturnsCachedTracks()
    {
        var db = TestHelpers.CreateDbContext();
        var service = new MakeMkvService(
            _runnerMock.Object,
            NullLoggerFactory.Instance,
            TestHelpers.CreateOptions(),
            db,
            Mock.Of<IHttpClientFactory>());

        var job = TestHelpers.CreateTestJob(j => j.DiscFingerprint = "VOL::12345");

        var discMetadata = new DiscMetadata
        {
            Fingerprint = "VOL::12345",
            VolumeLabel = "VOL",
            SectorCount = 12345,
            DiscType = "Dvd",
            CreatedAt = DateTime.UtcNow,
            LastUsedAt = DateTime.UtcNow,
            Tracks =
            [
                new DiscTrack
                {
                    TrackNumber = "0",
                    Length = 5400,
                    Chapters = 16,
                    FileSize = 5000000000L,
                    AspectRatio = "16:9",
                    Fps = 23.976,
                    Resolution = "1920x1080"
                }
            ]
        };
        db.DiscMetadata.Add(discMetadata);
        await db.SaveChangesAsync();
        db.Entry(discMetadata).State = EntityState.Detached;

        var tracks = await service.GetTrackInfoWithCacheAsync(job, "base");

        Assert.Single(tracks);
        Assert.Equal("0", tracks[0].TrackNumber);
        Assert.Equal(5400, tracks[0].Length);
        Assert.Equal(23.976, tracks[0].Fps);

        var refreshed = db.DiscMetadata.AsNoTracking().First(d => d.Fingerprint == "VOL::12345");
        Assert.NotEqual(default, refreshed.LastUsedAt);

        db.Dispose();
    }

    [Fact]
    public async Task GetTrackInfoWithCacheAsync_CacheMiss_CallsGetTrackInfo()
    {
        _runnerMock
            .Setup(r => r.RunStreamingAllAsync(
                "makemkvcon",
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Returns(ToStreamingAllAsync("TCOUNT:1", "TINFO:0,9,0,\"00:30:00\"", "TINFO:0,27,0,\"title00.mkv\""));

        var job = TestHelpers.CreateTestJob(j => j.DiscFingerprint = "UNIQUE::999");
        var tracks = await _service.GetTrackInfoWithCacheAsync(job, "base");

        Assert.Single(tracks);
        Assert.Equal(1800, tracks[0].Length);
    }

    [Fact]
    public async Task GetTrackInfoWithCacheAsync_NoFingerprint_CallsGetTrackInfo()
    {
        _runnerMock
            .Setup(r => r.RunStreamingAllAsync(
                "makemkvcon",
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Returns(ToStreamingAllAsync("TCOUNT:1", "TINFO:0,9,0,\"00:10:00\"", "TINFO:0,27,0,\"title00.mkv\""));

        var job = TestHelpers.CreateTestJob(j => j.DiscFingerprint = null);
        var tracks = await _service.GetTrackInfoWithCacheAsync(job, "base");

        Assert.Single(tracks);
    }

    // ── EnsureKeyAsync tests ─────────────────────────────────────

    [Fact]
    public async Task EnsureKeyAsync_WithConfiguredKey_WritesSettingsFile()
    {
        var originalPath = MakeMkvService.SettingsPath;
        var tempDir = Path.Combine(Path.GetTempPath(), "arm-test-makemkv", Guid.NewGuid().ToString());
        var settingsPath = Path.Combine(tempDir, ".MakeMKV", "settings.conf");

        try
        {
            MakeMkvService.SettingsPath = settingsPath;
            var settings = TestHelpers.CreateOptions(a => a.MakeMkvPermaKey = "T-PERMA-KEY");
            var service = new MakeMkvService(
                _runnerMock.Object,
                NullLoggerFactory.Instance,
                settings,
                _db,
                Mock.Of<IHttpClientFactory>());

            await service.EnsureKeyAsync();

            Assert.True(File.Exists(settingsPath));
            var content = await File.ReadAllTextAsync(settingsPath);
            Assert.Contains("T-PERMA-KEY", content);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
            MakeMkvService.SettingsPath = originalPath;
        }
    }

    // ── RipTrackAsync tests ──────────────────────────────────────

    [Fact]
    public async Task RipTrackAsync_CallsRunnerWithCorrectArgs()
    {
        _runnerMock
            .Setup(r => r.RunStreamingAsync(
                "makemkvcon",
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Returns(ToAsyncStream("PRGC:100,100", "PRGV:1,1,100,100"));

        var job = TestHelpers.CreateTestJob();
        var progress = new Mock<IProgress<int>>();

        await _service.RipTrackAsync(job, "0", "/output/path", "", 0, progress.Object);

        _runnerMock.Verify(r => r.RunStreamingAsync(
            "makemkvcon",
            It.Is<string>(a => a.Contains("mkv") && a.Contains("dev:/dev/sr0") && a.Contains("0") && a.Contains("/output/path")),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task RipTrackAsync_WithMkvArgs_IncludesArgs()
    {
        _runnerMock
            .Setup(r => r.RunStreamingAsync(
                "makemkvcon",
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Returns(ToAsyncStream());

        var job = TestHelpers.CreateTestJob();

        await _service.RipTrackAsync(job, "0", "/output/path", "--decrypt", 0);

        _runnerMock.Verify(r => r.RunStreamingAsync(
            "makemkvcon",
            It.Is<string>(a => a.Contains("--decrypt")),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()));
    }

    // ── RipAllTitlesAsync tests ──────────────────────────────────

    [Fact]
    public async Task RipAllTitlesAsync_CallsRunnerWithAllKeyword()
    {
        _runnerMock
            .Setup(r => r.RunStreamingAsync(
                "makemkvcon",
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Returns(ToAsyncStream());

        var job = TestHelpers.CreateTestJob();

        await _service.RipAllTitlesAsync(job, "/output/path", "", 0);

        _runnerMock.Verify(r => r.RunStreamingAsync(
            "makemkvcon",
            It.Is<string>(a => a.Contains(" all ") && a.Contains("/output/path")),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()));
    }

    // ── Static helper tests ──────────────────────────────────────

    [Theory]
    [InlineData("\"hello\"", "hello")]
    [InlineData("hello", "hello")]
    [InlineData("\"a\"", "a")]
    [InlineData("", "")]
    public void StripQuotes_RemovesSurroundingQuotes(string input, string expected)
    {
        Assert.Equal(expected, MakeMkvOutputParser.StripQuotes(input));
    }

    [Theory]
    [InlineData("01:30:00", 5400)]
    [InlineData("00:00:00", 0)]
    [InlineData("00:01:30", 90)]
    [InlineData("02:00:00", 7200)]
    public void HmsToSeconds_ConvertsCorrectly(string hms, int expected)
    {
        Assert.Equal(expected, MakeMkvOutputParser.HmsToSeconds(hms));
    }

    [Theory]
    [InlineData("a,b,c", new[] { "a", "b", "c" })]
    [InlineData("a,\"b,c\",d", new[] { "a", "b,c", "d" })]
    [InlineData("", new[] { "" })]
    [InlineData("single", new[] { "single" })]
    public void SplitCsv_SplitsCorrectly(string input, string[] expected)
    {
        Assert.Equal(expected, MakeMkvOutputParser.SplitCsv(input));
    }

    [Theory]
    [InlineData("a b c", new[] { "a", "b", "c" })]
    [InlineData("a \"b c\" d", new[] { "a", "b c", "d" })]
    [InlineData("", new string[0])]

    public void SplitArgs_SplitsCorrectly(string input, string[] expected)
    {
        var mi = typeof(MakeMkvService).GetMethod("SplitArgs",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        var result = mi!.Invoke(null, [input]) as string[];
        Assert.NotNull(result);
        Assert.Equal(expected, result);
    }

    // ── Progress reporting tests ─────────────────────────────────

    [Fact]
    public async Task RipTrackAsync_ReportsProgress()
    {
        _runnerMock
            .Setup(r => r.RunStreamingAsync(
                "makemkvcon",
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Returns(ToAsyncStream(
                "PRGC:50,100",
                "PRGV:1,1,75,100"));

        var job = TestHelpers.CreateTestJob();
        var reported = new List<int>();
        var progress = new Progress<int>(v => reported.Add(v));

        await _service.RipTrackAsync(job, "0", "/out", "", 0, progress);

        Assert.Contains(50, reported);
        Assert.Contains(75, reported);
    }

    [Fact]
    public async Task RipTrackAsync_NoProgressLines_ReportsOnlyCompletion()
    {
        _runnerMock
            .Setup(r => r.RunStreamingAsync(
                "makemkvcon",
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Returns(ToAsyncStream(
                "MSG:1005,0,1,\"MakeMKV v1.17.8\",\"\",",
                "TCOUNT:2"));

        var job = TestHelpers.CreateTestJob();
        var reported = new List<int>();
        var progress = new Progress<int>(v => reported.Add(v));

        await _service.RipTrackAsync(job, "0", "/out", "", 0, progress);

        // Non-progress lines must not report intermediate values, but the
        // service reports 100% once the rip completes.
        Assert.Equal([100], reported);
    }

    // ── MakeMkvRipResult capture tests ───────────────────────────

    [Fact]
    public async Task RipTrackAsync_CapturesReadErrorSkippedTitleAndCorruptSource()
    {
        _runnerMock
            .Setup(r => r.RunStreamingAsync(
                "makemkvcon",
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Returns(ToAsyncStream(
                "MSG:1005,0,1,\"MakeMKV v1.18.3\",\"\",",
                "MSG:2003,0,3,\"Error 'Scsi error - MEDIUM ERROR:UNRECOVERED READ ERROR' occurred while reading 'DVD' at offset '2381783040'\",\"Error '%1' occurred while reading '%2' at offset '%3'\",\"Scsi error - MEDIUM ERROR:UNRECOVERED READ ERROR\",\"DVD\",\"2381783040\"",
                "MSG:3015,0,2,\"Title #1 (1:49:15) was skipped due to navigation error\",\"Title #%1 (%2) was skipped due to navigation error\",\"1\",\"1:49:15\"",
                "MSG:3028,0,3,\"Title #2 was added (1 cell(s), 0:00:09)\",\"Title #%1 was added (%2 cell(s), %3)\",\"2\",\"1\",\"0:00:09\"",
                "MSG:4004,0,2,\"The source file '/VIDEO_TS/VTS_07_1.VOB' is corrupt or invalid at offset 4096, attempting to work around\",\"The source file '%1' is corrupt or invalid at offset %2, attempting to work around\",\"/VIDEO_TS/VTS_07_1.VOB\",\"4096\""));

        var job = TestHelpers.CreateTestJob();
        var result = await _service.RipTrackAsync(job, "0", "/out", "", 0);

        Assert.True(result.HadReadError);
        Assert.True(result.HadCorruptSource);
        Assert.True(result.HadSkippedTitles);
        Assert.Contains(result.SkippedTitles, s => s.Contains("navigation error"));
        Assert.Equal(1, result.TitlesSaved);
    }

    [Fact]
    public async Task RipTrackAsync_CleanRip_ReportsNoIssues()
    {
        _runnerMock
            .Setup(r => r.RunStreamingAsync(
                "makemkvcon",
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Returns(ToAsyncStream(
                "MSG:1005,0,1,\"MakeMKV v1.18.3\",\"\",",
                "PRGC:100,100",
                "TCOUNT:1"));

        var job = TestHelpers.CreateTestJob();
        var result = await _service.RipTrackAsync(job, "0", "/out", "", 0);

        Assert.False(result.HadReadError);
        Assert.False(result.HadCorruptSource);
        Assert.Empty(result.SkippedTitles);
        Assert.Equal(0, result.TitlesSaved);
    }

    [Fact]
    public async Task RipAllTitlesAsync_CapturesSkippedTitles()
    {
        _runnerMock
            .Setup(r => r.RunStreamingAsync(
                "makemkvcon",
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Returns(ToAsyncStream(
                "MSG:3015,0,2,\"Title #3 (0:12:34) was skipped due to navigation error\",\"Title #%1 (%2) was skipped due to navigation error\",\"3\",\"0:12:34\""));

        var job = TestHelpers.CreateTestJob();
        var result = await _service.RipAllTitlesAsync(job, "/out", "", 0);

        Assert.True(result.HadSkippedTitles);
        Assert.Single(result.SkippedTitles);
    }

    [Fact]
    public void MakeMkvRipResult_Merge_CombinesFlagsAndCounts()
    {
        var a = new MakeMkvRipResult();
        a.Capture(new MakeMkvMessage(2003, 0, 1, "read error", "read error", []));
        a.Capture(new MakeMkvMessage(3028, 0, 1, "added", "added", []));

        var b = new MakeMkvRipResult();
        b.Capture(new MakeMkvMessage(3015, 0, 1, "Title #2 skipped", "Title #%1 skipped", ["2"]));
        b.Capture(new MakeMkvMessage(4004, 0, 1, "corrupt", "corrupt", []));
        b.Capture(new MakeMkvMessage(3028, 0, 1, "added", "added", []));

        a.Merge(b);

        Assert.True(a.HadReadError);
        Assert.True(a.HadCorruptSource);
        Assert.True(a.HadSkippedTitles);
        Assert.Equal(2, a.TitlesSaved);
    }
}
