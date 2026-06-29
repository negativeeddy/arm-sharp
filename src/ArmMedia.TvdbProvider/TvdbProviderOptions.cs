namespace ArmMedia.TvdbProvider;

/// <summary>
/// Options for <see cref="TvdbProvider"/>.
/// Bind from the <c>Tvdb</c> configuration section.
/// </summary>
public sealed class TvdbProviderOptions
{
    /// <summary>Configuration section name used for binding.</summary>
    public const string SectionName = "Tvdb";

    /// <summary>
    /// Gets or sets the TVDB API key (v4 auth).
    /// Obtain one at https://thetvdb.com/api-information
    /// </summary>
    public string ApiKey { get; set; } = "";

    /// <summary>
    /// Gets or sets the TVDB API base URL.
    /// Defaults to the public v4 endpoint.
    /// </summary>
    public string ApiBaseUrl { get; set; } = "https://api4.thetvdb.com/v4";
}
