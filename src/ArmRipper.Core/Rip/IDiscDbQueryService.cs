namespace ArmRipper.Core.Rip;

/// <summary>Queries TheDiscDb GraphQL API for disc metadata by content hash.</summary>
public interface IDiscDbQueryService
{
    /// <summary>Query TheDiscDb by content hash. Returns null if no match.</summary>
    Task<DiscDbMediaResult?> QueryByHashAsync(string hash, CancellationToken ct = default);

    /// <summary>The base URL for the TheDiscDb GraphQL endpoint.</summary>
    string ApiBaseUrl { get; }
}
