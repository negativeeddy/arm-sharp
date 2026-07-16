using ArmRipper.Core.Models;

namespace ArmRipper.Core.Rip;

/// <summary>
/// Result of submitting a job's CRC64 to the remote ARM database.
/// </summary>
public record DatabaseSubmitResult
{
    public bool Success { get; init; }
    public string? Message { get; init; }
    public int? JobId { get; init; }
    public string? Title { get; init; }

    /// <summary>
    /// Categorises the result for UI display:
    /// "added" — new CRC successfully submitted to the remote DB,
    /// "already_exists" — CRC already present in the remote DB,
    /// "skipped" — already marked submitted locally,
    /// "failed" — submission failed.
    /// </summary>
    public string? Status { get; init; }
}

public interface IDatabaseSubmitService
{
    /// <summary>
    /// Submit a single job's CRC64 + metadata to the remote ARM database (1337server).
    /// Skips if the job has already been successfully submitted (CrcSubmitted stage is set).
    /// </summary>
    Task<DatabaseSubmitResult> SubmitJobAsync(Job job, CancellationToken ct = default);

    /// <summary>
    /// Submit all DVD jobs that have a CRC64, have a nice title, and have NOT yet been submitted.
    /// Returns a list of results for each attempted submission.
    /// </summary>
    Task<List<DatabaseSubmitResult>> SubmitPendingAsync(CancellationToken ct = default);
}
