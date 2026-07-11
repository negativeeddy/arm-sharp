using ArmMedia.OvidProvider;
using ArmRipper.Core.Infrastructure.Data;
using ArmRipper.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ArmRipper.Core.Rip;

/// <summary>
/// Submits OVID fingerprints to the community OVID database so newly-identified
/// discs contribute to the public corpus.
/// </summary>
public sealed class OvidSubmitService(
    ArmDbContext db,
    OvidApiClient ovidApiClient,
    ILoggerFactory loggerFactory) : IOvidSubmitService
{
    private readonly ILogger logger = loggerFactory.CreateLogger("OvidSubmitService");

    public async Task<OvidSubmitResult> SubmitJobAsync(Job job, CancellationToken ct = default)
    {
        // Skip if already submitted
        if (job.OvidSubmitted)
        {
            logger.LogInformation("Job {JobId} already submitted to OVID, skipping", job.Id);
            return new OvidSubmitResult { Success = true, JobId = job.Id, Message = "Already submitted", Status = "skipped" };
        }

        // Validate we have what we need
        if (string.IsNullOrWhiteSpace(job.OvidFingerprint))
        {
            var msg = $"Job {job.Id} has no OVID fingerprint, cannot submit";
            logger.LogWarning(msg);
            return new OvidSubmitResult { Success = false, JobId = job.Id, Message = msg, Status = "failed" };
        }

        var format = job.DiscType switch
        {
            DiscType.Dvd => "DVD",
            DiscType.Bluray => "Blu-ray",
            DiscType.Uhd => "UHD",
            _ => "Unknown"
        };

        try
        {
            // Register the fingerprint with the OVID API
            var (success, message, statusCode) = await ovidApiClient.RegisterFingerprintAsync(
                job.OvidFingerprint,
                format,
                job.Label,
                ct);

            if (success)
            {
                job.OvidSubmitted = true;
                await db.SaveChangesAsync(ct);

                var status = statusCode switch
                {
                    201 => "registered",
                    409 => "already_exists",
                    _ => "submitted"
                };

                logger.LogInformation(
                    "OVID submission for job {JobId} ({Fingerprint}): {Status}",
                    job.Id, job.OvidFingerprint, status);

                return new OvidSubmitResult
                {
                    Success = true,
                    JobId = job.Id,
                    Message = status switch
                    {
                        "registered" => "Fingerprint registered",
                        "already_exists" => "Already in OVID database",
                        _ => message
                    },
                    Status = status
                };
            }

            logger.LogWarning("OVID submission failed for job {JobId}: {Message}", job.Id, message);
            return new OvidSubmitResult { Success = false, JobId = job.Id, Message = message, Status = "failed" };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error submitting OVID fingerprint for job {JobId}", job.Id);
            return new OvidSubmitResult { Success = false, JobId = job.Id, Message = ex.Message, Status = "failed" };
        }
    }

    public async Task<List<OvidSubmitResult>> SubmitPendingAsync(CancellationToken ct = default)
    {
        var results = new List<OvidSubmitResult>();

        var pendingJobs = await db.Jobs
            .Where(j => !string.IsNullOrEmpty(j.OvidFingerprint) &&
                        (j.HasNiceTitle || !string.IsNullOrEmpty(j.TitleManual)) &&
                        !j.OvidSubmitted)
            .ToListAsync(ct);

        logger.LogInformation("Found {Count} jobs with OVID fingerprints pending submission", pendingJobs.Count);

        foreach (var job in pendingJobs)
        {
            ct.ThrowIfCancellationRequested();
            var result = await SubmitJobAsync(job, ct);
            results.Add(result);
        }

        return results;
    }
}
