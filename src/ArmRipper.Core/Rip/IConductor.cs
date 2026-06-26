namespace ArmRipper.Core.Rip;

public interface IConductor
{
    Task<int> RunAsync(string devicePath, CancellationToken ct = default);
    Task<int> RunForkedTranscodeAsync(int originalJobId, string rawFilePath, CancellationToken ct = default);
}
