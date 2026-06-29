namespace ArmMedia.Linting;

/// <summary>
/// Options controlling which lint rules are active and how failures are handled.
/// Bind from the <c>Linting</c> configuration section.
/// </summary>
public sealed class LintOptions
{
    /// <summary>Configuration section name used for binding.</summary>
    public const string SectionName = "Linting";

    /// <summary>
    /// Gets or sets a value indicating whether the ripping process should be
    /// aborted when any lint rule reports an <see cref="Models.LintSeverity.Error"/>.
    /// </summary>
    public bool FailOnError { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the ripping process should be
    /// aborted when any lint rule reports a <see cref="Models.LintSeverity.Warning"/>.
    /// </summary>
    public bool FailOnWarning { get; set; } = false;

    /// <summary>
    /// Gets or sets the set of rule IDs that should be suppressed (not evaluated).
    /// Example: <c>["TV004"]</c> to silence positional-fallback info messages.
    /// </summary>
    public HashSet<string> SuppressedRules { get; set; } = [];
    /// <summary>
    /// Gets or sets the expected runtime in seconds for a standard-length episode
    /// of the current series. Used by TV003 to detect duration mismatches.
    /// Set to <c>0</c> or leave unconfigured to disable this check.
    /// </summary>
    public int ExpectedEpisodeDurationSeconds { get; set; } = 0;

    /// <summary>
    /// Gets or sets the maximum ratio difference between the longest and shortest
    /// part of a multi-part episode before TV005 raises a warning.
    /// Default: <c>0.25</c> (25% difference triggers warning).
    /// </summary>
    public double MultiPartDurationToleranceRatio { get; set; } = 0.25;}
