using ArmMedia.Core.Models;

namespace ArmMedia.Naming.Abstractions;

/// <summary>
/// Generates an output file name (or relative path) for a single mapped track.
/// Implementations should be stateless and thread-safe.
/// </summary>
public interface IEpisodeRenamer
{
    /// <summary>
    /// Produces a file name (without extension) for <paramref name="track"/> using the
    /// supplied <paramref name="options"/>.  The caller is responsible for appending the
    /// correct container extension (e.g., <c>.mkv</c>).
    /// </summary>
    /// <param name="track">The mapped track to generate a name for.</param>
    /// <param name="options">Naming options including the template string.</param>
    /// <returns>A sanitised file name or relative path fragment.</returns>
    string Rename(MappedTrack track, NamingOptions options);
}
