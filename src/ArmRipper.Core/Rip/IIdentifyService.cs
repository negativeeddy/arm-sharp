using ArmRipper.Core.Models;

namespace ArmRipper.Core.Rip;

public interface IIdentifyService
{
    Task IdentifyAsync(Job job, CancellationToken ct = default);
    Task EjectAsync(Job job, CancellationToken ct = default);
}
