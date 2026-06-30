namespace ArmMedia.Naming;

/// <summary>
/// Options controlling how episode file names are generated.
/// Bind from the <c>Naming</c> configuration section.
/// </summary>
public sealed class NamingOptions
{
    /// <summary>Configuration section name used for binding.</summary>
    public const string SectionName = "Naming";

    /// <summary>
    /// Gets or sets the series title used in naming templates.
    /// When empty, the value from <see cref="Core.Models.EpisodeMap.SeriesTitle"/> is used.
    /// </summary>
    public string SeriesTitle { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the file name template for regular episodes.
    /// Supported tokens: <c>{Series}</c>, <c>{Season}</c>, <c>{Episode}</c>,
    /// <c>{Episodes}</c> (multi-part range, e.g. E03E04), <c>{Title}</c>.
    /// Format specifiers are supported: <c>{Season:D2}</c>.
    /// </summary>
    public string Template { get; set; } =
        "{Series} - S{Season:D2}E{Episode:D2} - {Title}";

    /// <summary>
    /// Gets or sets the template used when <see cref="Core.Models.MappedTrack.IsMultiPart"/> is <c>true</c>.
    /// </summary>
    public string MultiPartTemplate { get; set; } =
        "{Series} - S{Season:D2}{Episodes} - {Title}";

    /// <summary>
    /// Gets or sets the separator inserted between episode numbers in multi-part names
    /// (e.g., <c>E</c> produces <c>E03E04</c>).
    /// </summary>
    public string MultiPartSep { get; set; } = "E";

    /// <summary>
    /// Gets or sets the template used for extras and bonus features (Season 0 tracks).
    /// </summary>
    public string ExtraTemplate { get; set; } =
        "{Series} - S00 - {Title}";

    /// <summary>
    /// Gets or sets a value indicating whether characters invalid in Windows/Linux file names
    /// should be stripped or replaced in the generated name.
    /// </summary>
    public bool SanitizeFileName { get; set; } = true;

    // ── Built-in template presets ─────────────────────────────────────────────

    /// <summary>Plex-compatible naming template.</summary>
    public static NamingOptions Plex => new()
    {
        Template          = "{Series}/Season {Season:D2}/{Series} - S{Season:D2}E{Episode:D2} - {Title}",
        MultiPartTemplate = "{Series}/Season {Season:D2}/{Series} - S{Season:D2}{Episodes} - {Title}",
        ExtraTemplate     = "{Series}/Specials/{Series} - S00 - {Title}"
    };

    /// <summary>Kodi-compatible naming template.</summary>
    public static NamingOptions Kodi => new()
    {
        Template          = "{Series} S{Season:D2}E{Episode:D2} {Title}",
        MultiPartTemplate = "{Series} S{Season:D2}{Episodes} {Title}",
        ExtraTemplate     = "{Series} S00 {Title}"
    };

    /// <summary>Jellyfin-compatible naming template (default).</summary>
    public static NamingOptions Jellyfin => new()
    {
        Template          = "{Series} - S{Season:D2}E{Episode:D2} - {Title}",
        MultiPartTemplate = "{Series} - S{Season:D2}{Episodes} - {Title}",
        ExtraTemplate     = "{Series} - S00 - {Title}"
    };

    /// <summary>FileBot-style naming template.</summary>
    public static NamingOptions FileBot => new()
    {
        Template          = "{Series} - {Season:D2}x{Episode:D2} - {Title}",
        MultiPartTemplate = "{Series} - {Season:D2}x{Episodes} - {Title}",
        ExtraTemplate     = "{Series} - 00 - {Title}"
    };
}
