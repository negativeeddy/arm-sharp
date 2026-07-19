using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace ArmRipper.Core.Infrastructure;

public class CliProcessRunner(ILoggerFactory loggerFactory) : ICliProcessRunner
{
    private readonly ILogger logger = loggerFactory.CreateLogger(nameof(CliProcessRunner));
    public async Task<CliResult> RunAsync(
        string fileName,
        string arguments,
        string? workingDirectory = null,
        int timeoutMs = 120_000,
        CancellationToken ct = default)
    {
        logger.LogInformation("Running: {FileName} {Arguments}", fileName, arguments);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();

        using var _ = ct.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            logger.LogWarning("Process cancelled ({Name})", fileName);
        });

        var stdout = ReadAllLinesAsync(process.StandardOutput, ct);
        var stderr = ReadAllLinesAsync(process.StandardError, ct);

        var exited = process.WaitForExit(timeoutMs) && !ct.IsCancellationRequested;

        if (!exited)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            logger.LogWarning("Process timed out after {Timeout}ms: {FileName}", timeoutMs, fileName);
            return new CliResult(-1, string.Join("\n", await stdout), string.Join("\n", await stderr), true);
        }

        // Wait for async readers to finish (they may lag behind process exit)
        await Task.WhenAll(stdout, stderr);

        var stdOutList = await stdout;
        var stdErrList = await stderr;
        var stdOutStr = string.Join("\n", stdOutList);
        var stdErrStr = string.Join("\n", stdErrList);

        // Write command output to log file via Trace level (disabled by default to avoid I/O overhead)
        foreach (var line in stdOutList)
        {
            if (!string.IsNullOrWhiteSpace(line))
                logger.LogTrace("{FileName}: {Line}", fileName, line);
        }
        foreach (var line in stdErrList)
        {
            if (!string.IsNullOrWhiteSpace(line))
                logger.LogTrace("STDERR {FileName}: {Line}", fileName, line);
        }

        var result = new CliResult(process.ExitCode, stdOutStr, stdErrStr, false);
        logger.LogInformation("Process exited ({FileName}) code={Code}", fileName, result.ExitCode);
        return result;
    }

    private static async Task<List<string>> ReadAllLinesAsync(StreamReader reader, CancellationToken ct)
    {
        var lines = new List<string>();
        while (await reader.ReadLineAsync(ct) is { } line)
            lines.Add(line);
        return lines;
    }

    public async IAsyncEnumerable<string> RunStreamingAsync(
        string fileName,
        string arguments,
        string? workingDirectory = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        logger.LogInformation("Streaming: {FileName} {Arguments}", fileName, arguments);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();

        logger.LogDebug("Process started ({Name}): {Arguments}", fileName, arguments);

        // Use CancellationToken.None for stderr so it drains fully even after ct fires.
        var stderrTask = ReadAllLinesAsync(process.StandardError, CancellationToken.None);

        using var ctReg = ct.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            logger.LogWarning("Process cancelled ({Name})", fileName);
        });

        try
        {
            while (await process.StandardOutput.ReadLineAsync(ct) is { } line)
            {
                logger.LogTrace("{Name}: {Line}", fileName, line);
                yield return line;
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
            }
        }

        // Drain stderr before checking exit code — process may have been killed or exited.
        var stderr = await stderrTask;
        foreach (var errLine in stderr)
            logger.LogTrace("STDERR {FileName}: {Line}", fileName, errLine);

        process.WaitForExit();

        logger.LogInformation("Process exited ({Name}) code={Code}", fileName, process.ExitCode);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Process '{fileName}' exited with code {process.ExitCode}. " +
                $"Arguments: {arguments}");
        }
    }

    public async IAsyncEnumerable<(string? Line, bool IsStdErr, int? ExitCode)> RunStreamingAllAsync(
        string fileName,
        string arguments,
        string? workingDirectory = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        logger.LogInformation("Streaming both: {FileName} {Arguments}", fileName, arguments);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();

        using var ctReg = ct.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); } catch { }
        });

        var channel = System.Threading.Channels.Channel.CreateUnbounded<(string?, bool)>();

        var readerTask = Task.Run(async () =>
        {
            try
            {
                while (await process.StandardOutput.ReadLineAsync(ct) is { } line)
                    channel.Writer.TryWrite((line, false));

                while (await process.StandardError.ReadLineAsync(ct) is { } line)
                    channel.Writer.TryWrite((line, true));
            }
            finally
            {
                channel.Writer.Complete();
            }
        }, ct);

        try
        {
            await foreach (var (line, isErr) in channel.Reader.ReadAllAsync(ct))
                yield return (line, isErr, null);
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
            }
        }

        await readerTask;
        process.WaitForExit();
        var exitCode = process.ExitCode;
        logger.LogInformation("Process exited ({Name}) code={Code}", fileName, exitCode);
        yield return (null, false, exitCode);
    }

}
