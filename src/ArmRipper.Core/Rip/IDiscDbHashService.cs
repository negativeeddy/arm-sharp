using ArmRipper.Core.Models;

namespace ArmRipper.Core.Rip;

/// <summary>Computes TheDiscDb content hash from a mounted disc's filesystem.</summary>
public interface IDiscDbHashService
{
    /// <summary>
    /// Compute the TheDiscDb content hash from a mounted disc's filesystem.
    /// Returns null if the disc type is unsupported or mount point is unavailable.
    /// </summary>
    Task<string?> ComputeHashAsync(string mountPoint, DiscType discType, CancellationToken ct = default);
}
