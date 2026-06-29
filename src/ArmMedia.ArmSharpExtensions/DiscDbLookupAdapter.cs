using ArmMedia.Core.Abstractions;
using ArmMedia.Core.Models;
using ArmRipper.Core.Rip;
using Microsoft.Extensions.DependencyInjection;

namespace ArmMedia.ArmSharpExtensions;

/// <summary>
/// Adapts the existing <see cref="IDiscDbMappingService"/> from ArmRipper.Core
/// to the lightweight <see cref="IDiscDbLookupService"/> used by the provider layer.
/// This keeps <c>ArmMedia.DiscDbProvider</c> decoupled from the host's DiscDb
/// implementation and its transitive dependencies (EF Core, SQLite, etc.).
/// </summary>
/// <remarks>
/// <see cref="IDiscDbMappingService"/> is registered as scoped (it depends on
/// <see cref="ArmRipper.Core.Infrastructure.Data.ArmDbContext"/>), but our
/// provider pipeline is singleton.  This adapter resolves the mismatch by
/// creating a short-lived scope on each call so the DI container validates
/// correctly at startup.
/// </remarks>
public sealed class DiscDbLookupAdapter : IDiscDbLookupService
{
    private readonly IServiceScopeFactory _scopeFactory;

    /// <summary>
    /// Initialises a new instance of the <see cref="DiscDbLookupAdapter"/> class.
    /// </summary>
    /// <param name="scopeFactory">
    /// Service scope factory used to obtain an <see cref="IDiscDbMappingService"/>
    /// instance on each call.
    /// </param>
    public DiscDbLookupAdapter(IServiceScopeFactory scopeFactory)
        => _scopeFactory = scopeFactory;

    /// <inheritdoc />
    public async Task<DiscDbLookupResult?> LookupDiscAsync(
        string discId,
        CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var inner = scope.ServiceProvider.GetRequiredService<IDiscDbMappingService>();

        var record = await inner.GetCachedMappingAsync(discId, cancellationToken);
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
