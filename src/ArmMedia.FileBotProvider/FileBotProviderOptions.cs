namespace ArmMedia.FileBotProvider;

/// <summary>
/// Options for <see cref="FileBotProvider"/>.
/// Bind from the <c>FileBot</c> configuration section.
/// </summary>
public sealed class FileBotProviderOptions
{
    /// <summary>Configuration section name used for binding.</summary>
    public const string SectionName = "FileBot";

    /// <summary>
    /// Gets or sets the path to the optional FileBot sidecar map file.
    /// Supports the <c>{DiscId}</c> token which is replaced with
    /// <see cref="Core.Models.DiscContext.DiscId"/> at runtime.
    /// </summary>
    public string MapFilePath { get; set; } = "./filebot-map.json";
}
