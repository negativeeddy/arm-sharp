namespace ArmMedia.DvdCompareProvider;

/// <summary>
/// Options for <see cref="DvdCompareProvider"/>.
/// Bind from the <c>DvdCompare</c> configuration section.
/// </summary>
public sealed class DvdCompareProviderOptions
{
    /// <summary>Configuration section name used for binding.</summary>
    public const string SectionName = "DvdCompare";

    /// <summary>
    /// Gets or sets the dvdcompare.net comparison URL for the TV series season.
    /// When empty (default), the provider will search dvdcompare.net automatically
    /// using the series title and season from the disc context.
    /// Example: <c>https://dvdcompare.net/comparisons/film.php?fid=9923</c>
    /// </summary>
    public string ComparisonUrl { get; set; } = "";

    /// <summary>
    /// Gets or sets the runtime tolerance in seconds for matching tracks to
    /// episode listings. Tracks whose duration is within this tolerance of
    /// a listed episode runtime will be considered a match.
    /// Default is <c>60</c> seconds.
    /// </summary>
    public int RuntimeToleranceSeconds { get; set; } = 60;

    /// <summary>
    /// Gets or sets the ranking index of the release to use (0-based).
    /// Index 0 = first release on the comparison page (typically R1 America).
    /// Default is <c>0</c>.
    /// </summary>
    public int ReleaseIndex { get; set; } = 0;
}
