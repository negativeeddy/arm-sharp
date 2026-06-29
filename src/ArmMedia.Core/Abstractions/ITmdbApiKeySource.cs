namespace ArmMedia.Core.Abstractions;

/// <summary>
/// Resolves the TMDB API key from host configuration.
/// The implementation merges file config, database overrides, and fallbacks.
/// </summary>
public interface ITmdbApiKeySource
{
    /// <summary>
    /// Gets the TMDB API key, or <c>null</c> if not configured.
    /// This is evaluated each time it's read so DB overrides take effect.
    /// </summary>
    string? GetApiKey();
}
