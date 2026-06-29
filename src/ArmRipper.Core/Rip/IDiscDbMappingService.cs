namespace ArmRipper.Core.Rip;

/// <summary>Local cache of TheDiscDb disc mappings to avoid redundant API calls.</summary>
public interface IDiscDbMappingService
{
    /// <summary>Look up a cached mapping by content hash.</summary>
    Task<DiscDbMediaResult?> GetCachedMappingAsync(string contentHash, CancellationToken ct = default);

    /// <summary>Save a mapping to the cache after a successful API lookup.</summary>
    Task SaveMappingAsync(string contentHash, DiscDbMediaResult result, CancellationToken ct = default);

    /// <summary>Update the last-used timestamp for a cached mapping.</summary>
    Task TouchMappingAsync(string contentHash, CancellationToken ct = default);
}
