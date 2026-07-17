namespace ArmRipper.Core.Infrastructure;

public interface ICliProcessRunner
{
    Task<CliResult> RunAsync(
        string fileName,
        string arguments,
        string? workingDirectory = null,
        int timeoutMs = 120_000,
        CancellationToken ct = default);

    IAsyncEnumerable<(string? Line, int? ExitCode)> RunStreamingAsync(
        string fileName,
        string arguments,
        string? workingDirectory = null,
        CancellationToken ct = default);

    IAsyncEnumerable<(string? Line, bool IsStdErr, int? ExitCode)> RunStreamingAllAsync(
        string fileName,
        string arguments,
        string? workingDirectory = null,
        CancellationToken ct = default);
}
