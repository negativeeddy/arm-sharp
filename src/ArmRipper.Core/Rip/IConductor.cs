namespace ArmRipper.Core.Rip;

public interface IConductor
{
    Task<int> RunAsync(string devicePath, CancellationToken ct = default);
}
