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

        var tcs = new TaskCompletionSource<CliResult>();
        var stdout = new StringWriter();
        var stderr = new StringWriter();

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
            },
            EnableRaisingEvents = true
        };

        process.OutputDataReceived += (_, e) => stdout.WriteLine(e.Data);
        process.ErrorDataReceived += (_, e) => stderr.WriteLine(e.Data);

        process.Exited += (_, _) =>
        {
            tcs.TrySetResult(new CliResult(
                process.ExitCode,
                stdout.ToString(),
                stderr.ToString(),
                false));
            process.Dispose();
        };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs, cts.Token));

        if (completed != tcs.Task)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            logger.LogWarning("Process timed out after {Timeout}ms: {FileName}", timeoutMs, fileName);
            return new CliResult(-1, stdout.ToString(), stderr.ToString(), true);
        }

        var result = await tcs.Task;
        logger.LogDebug("Exit code {Code}: {FileName}", result.ExitCode, fileName);
        return result;
    }

    public async IAsyncEnumerable<string> RunStreamingAsync(
        string fileName,
        string arguments,
        string? workingDirectory = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        logger.LogDebug("Streaming: {FileName} {Arguments}", fileName, arguments);

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();

        while (await process.StandardOutput.ReadLineAsync(ct) is { } line)
        {
            yield return line;
        }

        process.WaitForExit();
        process.Dispose();
    }
}
