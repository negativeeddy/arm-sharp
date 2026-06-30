namespace ArmMedia.FileBotProvider;

/// <summary>
/// Result of a FileBot CLI invocation for file identification.
/// </summary>
/// <param name="ExitCode">Process exit code.</param>
/// <param name="StdOut">Standard output from the FileBot process.</param>
/// <param name="StdErr">Standard error from the FileBot process.</param>
public sealed record FileBotCliOutput(int ExitCode, string? StdOut, string? StdErr);
