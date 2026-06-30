namespace ArmMedia.Core.Orchestration;

/// <summary>
/// Configuration options for <see cref="EpisodeIdentificationOrchestrator"/>.
/// Bind from the <c>EpisodeIdentification</c> configuration section.
/// </summary>
public sealed class EpisodeIdentificationOptions
{
    /// <summary>Configuration section name used for binding.</summary>
    public const string SectionName = "EpisodeIdentification";

    /// <summary>
    /// Gets or sets the ordered list of provider names to invoke.
    /// Providers not in this list are invoked last in registration order.
    /// </summary>
    public List<string> ProviderOrder { get; set; } =
        ["DiscDb", "FileBot", "Tmdb", "Tvdb", "PositionalFallback"];

    /// <summary>
    /// Gets or sets a value indicating whether the pipeline should stop
    /// as soon as any provider returns a <see cref="Models.Confidence.Definitive"/> result.
    /// </summary>
    public bool ShortCircuitOnDefinitive { get; set; } = true;

    /// <summary>
    /// Gets or sets the maximum acceptable difference in seconds between a track's
    /// actual runtime and the expected episode runtime from metadata.
    /// </summary>
    public int RuntimeToleranceSeconds { get; set; } = 180;

    /// <summary>
    /// Gets or sets the maximum duration delta in seconds between two adjacent tracks
    /// to consider them candidates for multi-part episode merging.
    /// </summary>
    public int MultiPartDurationToleranceSeconds { get; set; } = 10;

    /// <summary>
    /// Gets or sets the maximum duration in seconds below which a track with no
    /// matching episode is automatically classified as an extra/bonus feature.
    /// </summary>
    public int ExtraMaxDurationSeconds { get; set; } = 600;
}
