using ArmMedia.Core.Models;
using ArmMedia.Linting.Abstractions;
using ArmMedia.Linting.Models;
using Microsoft.Extensions.Logging;

namespace ArmMedia.Linting;

/// <summary>
/// Default implementation of <see cref="ILintingEngine"/> that evaluates all
/// registered <see cref="ILintRule"/> implementations against the episode map.
/// </summary>
public sealed class DefaultLintingEngine : ILintingEngine
{
    private readonly IReadOnlyList<ILintRule>        _rules;
    private readonly ILogger<DefaultLintingEngine>   _logger;

    /// <summary>Initialises the engine with DI-injected rules.</summary>
    public DefaultLintingEngine(
        IEnumerable<ILintRule>          rules,
        ILogger<DefaultLintingEngine>   logger)
    {
        _rules  = rules.ToList();
        _logger = logger;
    }

    /// <inheritdoc/>
    public LintReport Lint(EpisodeMap map, LintOptions options)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(options);

        var issues = new List<LintIssue>();

        foreach (var rule in _rules)
        {
            if (options.SuppressedRules.Contains(rule.RuleId))
            {
                _logger.LogDebug("Lint rule {RuleId} suppressed by configuration.", rule.RuleId);
                continue;
            }

            _logger.LogDebug("Evaluating lint rule {RuleId}.", rule.RuleId);
            try
            {
                issues.AddRange(rule.Evaluate(map, options));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Lint rule {RuleId} threw an exception.", rule.RuleId);
            }
        }

        var report = new LintReport { Issues = issues };

        if (report.HasErrors)
            _logger.LogError("Lint found {Count} error(s). Ripping may be aborted.", issues.Count(i => i.Severity == LintSeverity.Error));
        else if (report.HasWarnings)
            _logger.LogWarning("Lint found {Count} warning(s).", issues.Count(i => i.Severity == LintSeverity.Warning));
        else
            _logger.LogInformation("Lint passed with no errors or warnings.");

        return report;
    }
}
