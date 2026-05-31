using ArmRipper.Core.Models;

namespace ArmRipper.Core.Rip;

public interface IArmRipperService
{
    Task<string> RipVisualMediaAsync(Job job, string logFile, bool hasDupes, bool protection, CancellationToken ct = default);
}
