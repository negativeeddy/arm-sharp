namespace ArmMedia.OvidProvider;

/// <summary>
/// Configuration options for <see cref="OvidProvider"/>.
/// Bind from the <c>OvidProvider</c> configuration section.
/// </summary>
public sealed class OvidProviderOptions
{
    /// <summary>Configuration section name used for binding.</summary>
    public const string SectionName = "OvidProvider";

    /// <summary>
    /// Base URL for the OVID API (default: https://api.oviddb.org).
    /// </summary>
    public string ApiUrl { get; set; } = "https://api.oviddb.org";

    /// <summary>
    /// Optional bearer token for authenticated operations (disc submission).
    /// </summary>
    public string? ApiToken { get; set; }

    /// <summary>
    /// HTTP request timeout in seconds (default: 30).
    /// </summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Whether the OVID provider is enabled (default: true).
    /// </summary>
    public bool Enabled { get; set; } = true;
}
