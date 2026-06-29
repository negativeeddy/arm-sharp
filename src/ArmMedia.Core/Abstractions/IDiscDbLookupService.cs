using ArmMedia.Core.Models;

namespace ArmMedia.Core.Abstractions;

/// <summary>
/// Minimal abstraction for looking up disc metadata from a DiscDb source.
/// This keeps providers decoupled from any particular DiscDb implementation
/// (e.g., the existing <c>ArmRipper.Core.Rip.IDiscDbMappingService</c>).
/// </summary>
public interface IDiscDbLookupService
{
    /// <summary>
    /// Looks up a disc by its content hash / disc identifier.
    /// Returns <c>null</c> when no record is found.
    /// </summary>
    Task<DiscDbLookupResult?> LookupDiscAsync(
        string discId,
        CancellationToken cancellationToken = default);
}
