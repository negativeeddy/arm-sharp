namespace ArmRipper.Core.Infrastructure;

public interface ICliProcessRunner
{
    Task<CliResult> RunAsync(
        string fileName,
        string arguments,
        string? workingDirectory = null,
        int timeoutMs = 120_000,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        CancellationToken ct = default);

    IAsyncEnumerable<string> RunStreamingAsync(
        string fileName,
        string arguments,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        CancellationToken ct = default);

    IAsyncEnumerable<(string? Line, bool IsStdErr, int? ExitCode)> RunStreamingAllAsync(
        string fileName,
        string arguments,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        CancellationToken ct = default);
}
