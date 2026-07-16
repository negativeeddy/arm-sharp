using ArmRipper.Core.Models;

namespace ArmRipper.Core.Rip;

/// <summary>
/// Result of a submission operation (CRC64 to ARM database, OVID fingerprint, etc.).
/// </summary>
public record SubmitResult
{
    public bool Success { get; init; }
    public string? Message { get; init; }
    public int? JobId { get; init; }
    public string? Title { get; init; }

    /// <summary>
    /// Categorises the result for UI display.
    /// Typical values: "added", "registered", "already_exists", "skipped", "failed".
    /// </summary>
    public string? Status { get; init; }
}

/// <summary>
/// Common abstraction for submitting job data to an external service/database.
/// </summary>
public interface ISubmitService
{
    /// <summary>
    /// Submit a single job's data to the external service.
    /// Skips if the job has already been successfully submitted.
    /// </summary>
    Task<SubmitResult> SubmitJobAsync(Job job, CancellationToken ct = default);

    /// <summary>
    /// Submit all pending (not-yet-submitted) jobs that have the required data.
    /// Returns a list of results for each attempted submission.
    /// </summary>
    Task<List<SubmitResult>> SubmitPendingAsync(CancellationToken ct = default);
}
