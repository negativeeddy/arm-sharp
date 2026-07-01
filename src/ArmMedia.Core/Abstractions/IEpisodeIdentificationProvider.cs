using ArmMedia.Core.Models;

namespace ArmMedia.Core.Abstractions;

/// <summary>
/// Identifies episodes for the tracks described by the disc context.
/// Providers are stateless and thread-safe; each returns results only for
/// tracks it can confidently resolve, leaving the rest to the orchestrator.
/// </summary>
public interface IEpisodeIdentificationProvider
{
    /// <summary>
    /// Gets the human-readable provider name used in logging and lint reports.
    /// </summary>
    string ProviderName { get; }

    /// <summary>
    /// Attempts to identify episode assignments for all tracks in <paramref name="context"/>.
    /// Returns an empty array if this provider has no data for the given disc.
    /// </summary>
    /// <param name="context">Disc and track information gathered before ripping begins.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>
    /// An array of <see cref="ProviderResult"/> records, one per track this provider could identify.
    /// </returns>
    Task<ProviderResult[]> IdentifyAsync(DiscContext context, CancellationToken cancellationToken = default);
}
