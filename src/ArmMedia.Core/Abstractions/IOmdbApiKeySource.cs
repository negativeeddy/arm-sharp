namespace ArmMedia.Core.Abstractions;

/// <summary>
/// Resolves the OMDB API key from host configuration.
/// </summary>
public interface IOmdbApiKeySource
{
    /// <summary>Gets the OMDB API key, or <c>null</c> if not configured.</summary>
    string? GetApiKey();
}
