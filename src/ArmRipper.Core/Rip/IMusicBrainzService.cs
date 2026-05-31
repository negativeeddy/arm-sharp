using ArmRipper.Core.Models;

namespace ArmRipper.Core.Rip;

public interface IMusicBrainzService
{
    Task<string> IdentifyAsync(Job job, CancellationToken ct = default);
}
