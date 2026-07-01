using ArmMedia.Core.Models;

namespace ArmMedia.Core.Abstractions;

/// <summary>
/// Runs the ordered provider pipeline and returns a merged <see cref="EpisodeMap"/>
/// for all tracks on the disc described by the disc context.
/// </summary>
public interface IEpisodeIdentificationOrchestrator
{
    /// <summary>
    /// Executes all registered <see cref="IEpisodeIdentificationProvider"/> implementations
    /// in configured order, merges results by confidence level, applies multi-part and
    /// extras detection, and returns the final <see cref="EpisodeMap"/>.
    /// </summary>
    /// <param name="context">Disc and track information gathered before ripping begins.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>
    /// A merged <see cref="EpisodeMap"/> with one <see cref="MappedTrack"/> per disc track.
    /// </returns>
    Task<EpisodeMap> IdentifyAsync(DiscContext context, CancellationToken cancellationToken = default);
}
