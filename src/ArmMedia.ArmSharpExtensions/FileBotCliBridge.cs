using ArmMedia.FileBotProvider;
using ArmRipper.Core.Infrastructure;

namespace ArmMedia.ArmSharpExtensions;

/// <summary>
/// Bridges the host's <see cref="ICliProcessRunner"/> to the
/// <see cref="FileBotCliRunner"/> delegate expected by <see cref="FileBotCliService"/>.
/// </summary>
public static class FileBotCliBridge
{
    /// <summary>
    /// Creates a <see cref="FileBotCliRunner"/> delegate that wraps the host's
    /// <see cref="ICliProcessRunner"/>.
    /// </summary>
    public static FileBotCliRunner CreateRunner(ICliProcessRunner runner)
    {
        return async (args, workingDir, timeoutMs, ct) =>
        {
            var result = await runner.RunAsync("filebot", args, workingDir, timeoutMs, ct);
            return new FileBotCliOutput(result.ExitCode, result.StdOut, result.StdErr);
        };
    }
}
