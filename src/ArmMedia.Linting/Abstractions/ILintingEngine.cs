using ArmMedia.Core.Models;
using ArmMedia.Linting.Models;

namespace ArmMedia.Linting.Abstractions;

/// <summary>
/// Runs all registered lint rules against an <see cref="EpisodeMap"/> and
/// returns a <see cref="LintReport"/> describing any issues found.
/// </summary>
public interface ILintingEngine
{
    /// <summary>
    /// Evaluates all lint rules and returns a consolidated <see cref="LintReport"/>.
    /// </summary>
    /// <param name="map">The merged episode map to lint.</param>
    /// <param name="options">Options controlling which rules are active and severity overrides.</param>
    /// <returns>A <see cref="LintReport"/> containing all findings.</returns>
    LintReport Lint(EpisodeMap map, LintOptions options);
}

/// <summary>
/// A single stateless lint rule evaluated by <see cref="ILintingEngine"/>.
/// Implementations should be registered with DI and injected into the engine.
/// </summary>
public interface ILintRule
{
    /// <summary>Gets the unique rule identifier (e.g., <c>TV001</c>).</summary>
    string RuleId { get; }

    /// <summary>
    /// Evaluates the rule against <paramref name="map"/> and yields zero or more issues.
    /// </summary>
    IEnumerable<LintIssue> Evaluate(EpisodeMap map, LintOptions options);
}
