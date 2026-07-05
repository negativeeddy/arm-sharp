using ArmRipper.Core.Models;

namespace ArmRipper.Core.Rip;

/// <summary>
/// Result of submitting a job's OVID fingerprint to the OVID database.
/// </summary>
public record OvidSubmitResult
{
    public bool Success { get; init; }
    public string? Message { get; init; }
    public int? JobId { get; init; }

    /// <summary>
    /// Categorises the result for UI display:
    /// "registered" — new fingerprint successfully registered,
    /// "already_exists" — fingerprint already in the OVID DB,
    /// "skipped" — already marked submitted locally,
    /// "failed" — submission failed.
    /// </summary>
    public string? Status { get; init; }
}

/// <summary>
/// Service for submitting OVID fingerprints to the community OVID database.
/// Follows the same pattern as <see cref="IDatabaseSubmitService"/>.
/// </summary>
public interface IOvidSubmitService
{
    /// <summary>
    /// Submit a single job's OVID fingerprint to the OVID API.
    /// Skips if the job has already been successfully submitted (OvidSubmitted is true).
    /// </summary>
    Task<OvidSubmitResult> SubmitJobAsync(Job job, CancellationToken ct = default);

    /// <summary>
    /// Submit all jobs that have an OVID fingerprint, have a nice title, and have NOT yet been submitted.
    /// Returns a list of results for each attempted submission.
    /// </summary>
    Task<List<OvidSubmitResult>> SubmitPendingAsync(CancellationToken ct = default);
}
