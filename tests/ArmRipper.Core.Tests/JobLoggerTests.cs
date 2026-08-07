using ArmRipper.Core.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArmRipper.Core.Tests;

public sealed class JobLoggerTests
{
    private static JobLogger CreateLogger(out string logPath)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"joblogger-{Guid.NewGuid():N}");
        var jobId = Guid.NewGuid().ToString("N");
        logPath = Path.Combine(dir, $"arm_job_{jobId}.log");
        return new JobLogger(jobId, dir, NullLogger.Instance);
    }

    [Fact]
    public async Task ConcurrentLogs_AllLinesPreserved()
    {
        var logger = CreateLogger(out var logPath);

        const int messageCount = 200;
        var messages = Enumerable.Range(0, messageCount)
            .Select(i => $"message-{i}-{new string('x', 100)}")
            .ToArray();

        await Task.WhenAll(messages.Select(m =>
            Task.Run(() => logger.Log(LogLevel.Information, 0, m, null, (s, _) => s!))));

        logger.Dispose();

        var lines = await File.ReadAllLinesAsync(logPath);
        var recovered = lines.Select(l => l.Split("] ").Last()).ToArray();

        Assert.Equal(messageCount, lines.Length);
        Assert.Equal(messages.OrderBy(m => m), recovered.OrderBy(m => m));
    }

    [Fact]
    public void Log_AfterDispose_DoesNotThrow()
    {
        var logger = CreateLogger(out var logPath);
        logger.Dispose();

        var ex = Record.Exception(() =>
            logger.Log(LogLevel.Information, 0, "late message", null, (s, _) => s!));

        Assert.Null(ex);
    }
}
