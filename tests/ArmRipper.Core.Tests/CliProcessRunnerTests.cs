using ArmRipper.Core.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArmRipper.Core.Tests;

public class CliProcessRunnerTests
{
    private readonly CliProcessRunner _runner = new(NullLoggerFactory.Instance);

    [Fact]
    public async Task RunStreamingAllAsync_ReadsBothStdoutAndStderr()
    {
        var results = new List<(string? Line, bool IsStdErr, int? ExitCode)>();

        await foreach (var item in _runner.RunStreamingAllAsync(
            "bash", "-c \"echo stdout1; echo stdout2 >&2; echo stdout2; echo stderr2 >&2\""))
        {
            results.Add(item);
        }

        var stdout = results.Where(r => !r.IsStdErr && r.ExitCode == null).Select(r => r.Line).ToList();
        var stderr = results.Where(r => r.IsStdErr && r.ExitCode == null).Select(r => r.Line).ToList();

        Assert.Equal(["stdout1", "stdout2"], stdout);
        Assert.Equal(["stdout2", "stderr2"], stderr);

        // Last item should be the exit code
        var last = results.Last();
        Assert.NotNull(last.ExitCode);
        Assert.Equal(0, last.ExitCode);
    }

    [Fact]
    public async Task RunStreamingAllAsync_ConcurrentOutputDoesNotDeadlock()
    {
        // Write a large amount to both streams simultaneously — this would deadlock
        // with the old sequential read pattern if stderr fills the pipe buffer first.
        var cmd = "for i in $(seq 1 1000); do echo \"stdout-$i\"; echo \"stderr-$i\" >&2; done";

        var results = new List<(string? Line, bool IsStdErr, int? ExitCode)>();

        await foreach (var item in _runner.RunStreamingAllAsync("bash", $"-c \"{cmd}\""))
        {
            results.Add(item);
        }

        var stdout = results.Where(r => !r.IsStdErr && r.ExitCode == null).Select(r => r.Line).ToList();
        var stderr = results.Where(r => r.IsStdErr && r.ExitCode == null).Select(r => r.Line).ToList();

        Assert.Equal(1000, stdout.Count);
        Assert.Equal(1000, stderr.Count);
        Assert.All(stdout, line => Assert.StartsWith("stdout-", line));
        Assert.All(stderr, line => Assert.StartsWith("stderr-", line));

        var last = results.Last();
        Assert.NotNull(last.ExitCode);
        Assert.Equal(0, last.ExitCode);
    }

    [Fact]
    public async Task RunStreamingAllAsync_ExcessiveStderrDoesNotDeadlock()
    {
        // Simulate a process that writes heavily to stderr while stdout has
        // minimal output — the exact scenario that triggers the pipe deadlock.
        var cmd = "echo stdout-done; for i in $(seq 1 5000); do echo \"stderr-line-$i\" >&2; done";

        var results = new List<(string? Line, bool IsStdErr, int? ExitCode)>();

        await foreach (var item in _runner.RunStreamingAllAsync("bash", $"-c \"{cmd}\""))
        {
            results.Add(item);
        }

        var stdout = results.Where(r => !r.IsStdErr && r.ExitCode == null).Select(r => r.Line).ToList();
        var stderr = results.Where(r => r.IsStdErr && r.ExitCode == null).Select(r => r.Line).ToList();

        Assert.Single(stdout);
        Assert.Equal("stdout-done", stdout[0]);
        Assert.Equal(5000, stderr.Count);

        var last = results.Last();
        Assert.NotNull(last.ExitCode);
        Assert.Equal(0, last.ExitCode);
    }

    [Fact]
    public async Task RunStreamingAllAsync_ProcessExitCode_IsCaptured()
    {
        var results = new List<(string? Line, bool IsStdErr, int? ExitCode)>();

        await foreach (var item in _runner.RunStreamingAllAsync("bash", "-c \"echo out; echo err >&2; exit 42\""))
        {
            results.Add(item);
        }

        var exitItem = results.Last();
        Assert.NotNull(exitItem.ExitCode);
        Assert.Equal(42, exitItem.ExitCode);
    }
}
