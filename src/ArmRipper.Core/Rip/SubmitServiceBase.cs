using ArmRipper.Core.Infrastructure.Data;
using ArmRipper.Core.Models;
using Microsoft.Extensions.Logging;

namespace ArmRipper.Core.Rip;

/// <summary>
/// Base class for submit services that share the same "query pending → submit each" pattern.
/// Derived classes implement <see cref="SubmitJobAsync"/>, <see cref="GetPendingJobsAsync"/>,
/// and <see cref="GetServiceName"/>; the base provides <see cref="SubmitPendingAsync"/>.
/// </summary>
public abstract class SubmitServiceBase(ArmDbContext db, ILogger logger) : ISubmitService
{
    /// <summary>
    /// Database context for persisting submission state.
    /// </summary>
    protected ArmDbContext Db { get; } = db;

    /// <summary>
    /// Logger accessible to derived services.
    /// </summary>
    protected ILogger Logger { get; } = logger;

    public abstract Task<SubmitResult> SubmitJobAsync(Job job, CancellationToken ct = default);

    public async Task<List<SubmitResult>> SubmitPendingAsync(CancellationToken ct = default)
    {
        var results = new List<SubmitResult>();
        var pendingJobs = await GetPendingJobsAsync(ct);

        Logger.LogInformation(
            "Found {Count} pending jobs for {Service}",
            pendingJobs.Count,
            GetServiceName());

        foreach (var job in pendingJobs)
        {
            ct.ThrowIfCancellationRequested();
            var result = await SubmitJobAsync(job, ct);
            results.Add(result);
        }

        return results;
    }

    /// <summary>
    /// Returns the list of jobs that still need to be submitted.
    /// </summary>
    protected abstract Task<List<Job>> GetPendingJobsAsync(CancellationToken ct);

    /// <summary>
    /// Short human-readable name used in log output (e.g. "DatabaseSubmitService").
    /// </summary>
    protected abstract string GetServiceName();
}
