namespace ArmMedia.TmdbProvider;

/// <summary>
/// Options for <see cref="TmdbProvider"/>.
/// Bind from the <c>TMDB</c> configuration section.
/// </summary>
public sealed class TmdbProviderOptions
{
    /// <summary>Configuration section name used for binding.</summary>
    public const string SectionName = "Tmdb";

    /// <summary>
    /// Gets or sets the TMDB API key (v3 auth).
    /// Obtain one at https://www.themoviedb.org/settings/api
    /// </summary>
    public string ApiKey { get; set; } = "";
}
