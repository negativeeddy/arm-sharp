namespace ArmRipper.Core.Infrastructure;

public record CliResult(int ExitCode, string StdOut, string StdErr, bool TimedOut);
