using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace ArmRipper.Core.Infrastructure;

public class CliProcessRunner(ILogger<CliProcessRunner> logger) : ICliProcessRunner
{
    public async Task<CliResult> RunAsync(
        string fileName,
        string arguments,
        string? workingDirectory = null,
        int timeoutMs = 120_000,
        CancellationToken ct = default)
    {
        logger.LogDebug("Running: {FileName} {Arguments}", fileName, arguments);

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

        var result = new CliResult(process.ExitCode, string.Join("\n", await stdout), string.Join("\n", await stderr), false);
        logger.LogDebug("Exit code {Code}: {FileName}", result.ExitCode, fileName);
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

        var process = new Process
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

        // TODO: is this a race condition if the process writes to stdout before we start reading? Should we start reading before starting the process?
        process.Start();

        using var _ = ct.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            logger.LogWarning("Process cancelled ({Name})", fileName);
        });

        var stderrTask = ReadAllLinesAsync(process.StandardError, ct);

        while (await process.StandardOutput.ReadLineAsync(ct) is { } line)
        {
            yield return line;
        }

        process.WaitForExit();

        var stderr = await stderrTask;
        if (stderr.Count > 0)
            logger.LogWarning("Process stderr ({Name}): {Lines}", fileName, string.Join("\n", stderr));
        logger.LogInformation("Process exited ({Name}) code={Code}", fileName, process.ExitCode);

        process.Dispose();
    }

    public async IAsyncEnumerable<(string? Line, bool IsStdErr, int? ExitCode)> RunStreamingAllAsync(
        string fileName,
        string arguments,
        string? workingDirectory = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        logger.LogInformation("Streaming both: {FileName} {Arguments}", fileName, arguments);

        var process = new Process
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

        await foreach (var (line, isErr) in channel.Reader.ReadAllAsync(ct))
            yield return (line, isErr, null);

        await readerTask;
        process.WaitForExit();
        var exitCode = process.ExitCode;
        logger.LogInformation("Process exited ({Name}) code={Code}", fileName, exitCode);
        process.Dispose();
        yield return (null, false, exitCode);
    }
}
