namespace ArmMedia.FileBotProvider;

/// <summary>
/// Runs a FileBot CLI command and returns the raw output.
/// Implementations bridge to the host's process runner (e.g. <c>ICliProcessRunner</c>).
/// </summary>
/// <param name="arguments">FileBot CLI arguments (without the <c>filebot</c> executable name).</param>
/// <param name="workingDirectory">Optional working directory for the process.</param>
/// <param name="timeoutMs">Maximum time to wait for the process, in milliseconds.</param>
/// <param name="ct">Cancellation token.</param>
/// <returns>The CLI exit code, stdout, and stderr.</returns>
public delegate Task<FileBotCliOutput> FileBotCliRunner(
    string arguments,
    string? workingDirectory,
    int timeoutMs,
    CancellationToken ct);
