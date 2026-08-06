using System.Linq.Expressions;

namespace ArmRipper.Core.Models;

/// <summary>
/// EF Core-compatible expression helpers for pipe-delimited <see cref="Job.CompletedStages"/>
/// queries.  These generate translatable SQL (LIKE/StartsWith/EndsWith/Contains) that respects
/// the pipe delimiter, so substring collisions (e.g. "PreRip" vs "Rip") don't cause false matches.
/// </summary>
public static class JobStageQueryHelper
{
    /// <summary>
    /// Returns an expression that matches jobs whose <c>CompletedStages</c> contains the
    /// given stage name, respecting pipe-delimiter boundaries.
    /// </summary>
    /// <example>
    /// db.Jobs.Where(JobStageQueryHelper.HasCompletedStage(RipStage.Rip))
    /// </example>
    public static Expression<Func<Job, bool>> HasCompletedStage(RipStage stage)
    {
        var name = stage.ToString();
        return j => !string.IsNullOrEmpty(j.CompletedStages) && (
            j.CompletedStages == name ||
            j.CompletedStages.StartsWith(name + "|") ||
            j.CompletedStages.EndsWith("|" + name) ||
            j.CompletedStages.Contains("|" + name + "|"));
    }

    /// <summary>
    /// Returns an expression that matches jobs whose <c>CompletedStages</c> does <b>not</b>
    /// contain the given stage name.  Useful for counting/filtering unfinished stages.
    /// </summary>
    /// <example>
    /// db.Jobs.Where(JobStageQueryHelper.NotHasCompletedStage(RipStage.CrcSubmitted))
    /// </example>
    public static Expression<Func<Job, bool>> NotHasCompletedStage(RipStage stage)
    {
        var name = stage.ToString();
        return j => string.IsNullOrEmpty(j.CompletedStages) || !(
            j.CompletedStages == name ||
            j.CompletedStages.StartsWith(name + "|") ||
            j.CompletedStages.EndsWith("|" + name) ||
            j.CompletedStages.Contains("|" + name + "|"));
    }
}
