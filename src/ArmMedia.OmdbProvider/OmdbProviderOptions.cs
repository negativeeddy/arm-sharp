namespace ArmMedia.OmdbProvider;

/// <summary>
/// Options for <see cref="OmdbProvider"/>.
/// Bind from the <c>Omdb</c> configuration section.
/// </summary>
public sealed class OmdbProviderOptions
{
    /// <summary>Configuration section name used for binding.</summary>
    public const string SectionName = "Omdb";

    /// <summary>
    /// Gets or sets the OMDB API key.
    /// Matches the same key used by IdentifyService for movie metadata.
    /// </summary>
    public string ApiKey { get; set; } = "";
}
