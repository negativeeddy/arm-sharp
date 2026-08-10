using ArmRipper.Core.Infrastructure;
using ArmRipper.Core.Rip;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ArmRipper.Core.Tests;

public sealed class FfmpegServiceTests
{
    private readonly Mock<ICliProcessRunner> _runnerMock;
    private readonly FfmpegService _service;

    public FfmpegServiceTests()
    {
        _runnerMock = new Mock<ICliProcessRunner>();

        _runnerMock
            .Setup(r => r.RunAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CliResult(0, "", "", false));

        _service = new FfmpegService(
            _runnerMock.Object,
            NullLoggerFactory.Instance,
            TestHelpers.CreateDbContext(),
            TestHelpers.CreateOptions(),
            Mock.Of<ITranscodeSlotLimiter>());
    }

    [Fact]
    public async Task ProbeDurationAsync_ValidOutput_ReturnsDuration()
    {
        _runnerMock
            .Setup(r => r.RunAsync("ffprobe",
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CliResult(0, "6547.200000\n", "", false));

        var file = Path.Combine(Path.GetTempPath(), "fixture.mkv");
        File.WriteAllBytes(file, []);
        try
        {
            var duration = await _service.ProbeDurationAsync(file);
            Assert.Equal(6547.2, duration);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task ProbeDurationAsync_NonZeroExit_ReturnsNull()
    {
        _runnerMock
            .Setup(r => r.RunAsync("ffprobe",
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CliResult(1, "", "error", false));

        var file = Path.Combine(Path.GetTempPath(), "fixture.mkv");
        File.WriteAllBytes(file, []);
        try
        {
            Assert.Null(await _service.ProbeDurationAsync(file));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task ProbeDurationAsync_UnparseableOutput_ReturnsNull()
    {
        _runnerMock
            .Setup(r => r.RunAsync("ffprobe",
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CliResult(0, "N/A\n", "", false));

        var file = Path.Combine(Path.GetTempPath(), "fixture.mkv");
        File.WriteAllBytes(file, []);
        try
        {
            Assert.Null(await _service.ProbeDurationAsync(file));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task ProbeDurationAsync_MissingFile_ReturnsNull()
    {
        var missing = Path.Combine(Path.GetTempPath(), "does-not-exist.mkv");
        Assert.Null(await _service.ProbeDurationAsync(missing));
        _runnerMock.Verify(r => r.RunAsync(
            "ffprobe", It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ProbeDurationAsync_Throws_ReturnsNull()
    {
        _runnerMock
            .Setup(r => r.RunAsync("ffprobe",
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("boom"));

        var file = Path.Combine(Path.GetTempPath(), "fixture.mkv");
        File.WriteAllBytes(file, []);
        try
        {
            Assert.Null(await _service.ProbeDurationAsync(file));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task ProbeDurationAsync_QuotesFilePathAndRequestsDuration()
    {
        var dir = Path.Combine(Path.GetTempPath(), "arm-ffprobe-test", Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "title_t00.mkv");
        File.WriteAllBytes(file, []);
        try
        {
            await _service.ProbeDurationAsync(file);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }

        _runnerMock.Verify(r => r.RunAsync(
            "ffprobe",
            It.Is<string>(a =>
                a.Contains("-show_entries format=duration", StringComparison.Ordinal) &&
                a.Contains($"\"{file}\"", StringComparison.Ordinal)),
            It.IsAny<string?>(),
            It.IsAny<int>(),
            It.IsAny<CancellationToken>()));
    }
}
