namespace ArmMedia.Linting.Models;

/// <summary>
/// The result of running all lint rules against an <see cref="Core.Models.EpisodeMap"/>.
/// </summary>
public sealed class LintReport
{
    /// <summary>Gets the ordered list of issues found during linting.</summary>
    public IReadOnlyList<LintIssue> Issues { get; init; } = [];

    /// <summary>Gets a value indicating whether any errors were reported.</summary>
    public bool HasErrors   => Issues.Any(i => i.Severity == LintSeverity.Error);

    /// <summary>Gets a value indicating whether any warnings were reported.</summary>
    public bool HasWarnings => Issues.Any(i => i.Severity == LintSeverity.Warning);

    /// <summary>Gets a value indicating whether the report is clean (no errors or warnings).</summary>
    public bool IsClean => !HasErrors && !HasWarnings;
}

/// <summary>A single lint finding raised by a lint rule.</summary>
public sealed class LintIssue
{
    /// <summary>Gets the severity of this issue.</summary>
    public required LintSeverity Severity { get; init; }

    /// <summary>
    /// Gets the rule identifier (e.g., <c>TV001</c>).
    /// Used for suppression configuration and structured output.
    /// </summary>
    public required string RuleId { get; init; }

    /// <summary>Gets the human-readable description of the issue.</summary>
    public required string Message { get; init; }

    /// <summary>
    /// Gets the zero-based track index this issue relates to,
    /// or <c>null</c> for disc-level issues.
    /// </summary>
    public int? TrackIndex { get; init; }
}

/// <summary>Severity levels for lint issues.</summary>
public enum LintSeverity
{
    /// <summary>Informational; does not indicate a problem.</summary>
    Info = 0,

    /// <summary>Potential problem; ripping proceeds unless <c>FailOnWarning</c> is set.</summary>
    Warning = 1,

    /// <summary>Definitive problem; ripping is aborted when <c>FailOnError</c> is set.</summary>
    Error = 2
}
