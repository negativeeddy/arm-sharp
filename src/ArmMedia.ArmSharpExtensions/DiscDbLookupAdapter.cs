using ArmMedia.Core.Abstractions;
using ArmMedia.Core.Models;
using ArmRipper.Core.Rip;

namespace ArmMedia.ArmSharpExtensions;

/// <summary>
/// Adapts the existing <see cref="IDiscDbMappingService"/> from ArmRipper.Core
/// to the lightweight <see cref="IDiscDbLookupService"/> used by the provider layer.
/// This keeps <c>ArmMedia.DiscDbProvider</c> decoupled from the host's DiscDb
/// implementation and its transitive dependencies (EF Core, SQLite, etc.).
/// </summary>
public sealed class DiscDbLookupAdapter : IDiscDbLookupService
{
    private readonly IDiscDbMappingService _inner;

    /// <summary>
    /// Initialises a new instance of the <see cref="DiscDbLookupAdapter"/> class.
    /// </summary>
    /// <param name="inner">The host's DiscDb mapping service to wrap.</param>
    public DiscDbLookupAdapter(IDiscDbMappingService inner) => _inner = inner;

    /// <inheritdoc />
    public async Task<DiscDbLookupResult?> LookupDiscAsync(
        string discId,
        CancellationToken cancellationToken = default)
    {
        var record = await _inner.GetCachedMappingAsync(discId, cancellationToken);
        if (record is null)
            return null;

        var tracks = new List<DiscDbLookupTrack>();

        foreach (var release in record.Releases ?? [])
        {
            foreach (var disc in release.Discs ?? [])
            {
                foreach (var title in disc.Titles ?? [])
                {
                    if (title.Item is null)
                        continue;

                    tracks.Add(new DiscDbLookupTrack
                    {
                        TrackIndex  = title.Index,
                        Title       = title.Item.Title,
                        Season      = title.Item.Season,
                        Episode     = title.Item.Episode,
                        ContentType = title.Item.Type
                    });
                }
            }
        }

        return new DiscDbLookupResult
        {
            Title  = record.Title,
            Year   = record.Year,
            Tracks = tracks
        };
    }
}
