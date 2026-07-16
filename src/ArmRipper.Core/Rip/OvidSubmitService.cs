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
    ILoggerFactory loggerFactory)
    : SubmitServiceBase(db, loggerFactory.CreateLogger("OvidSubmitService")), IOvidSubmitService
{
    public override async Task<SubmitResult> SubmitJobAsync(Job job, CancellationToken ct = default)
    {
        // Skip if already submitted
        if (job.OvidSubmitted)
        {
            Logger.LogInformation("Job {JobId} already submitted to OVID, skipping", job.Id);
            return new SubmitResult { Success = true, JobId = job.Id, Title = job.Title, Message = "Already submitted", Status = "skipped" };
        }

        // Validate we have what we need
        if (string.IsNullOrWhiteSpace(job.OvidFingerprint))
        {
            var msg = $"Job {job.Id} has no OVID fingerprint, cannot submit";
            Logger.LogWarning(msg);
            return new SubmitResult { Success = false, JobId = job.Id, Title = job.Title, Message = msg, Status = "failed" };
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
                await Db.SaveChangesAsync(ct);

                var status = statusCode switch
                {
                    201 => "registered",
                    409 => "already_exists",
                    _ => "submitted"
                };

                Logger.LogInformation(
                    "OVID submission for job {JobId} ({Fingerprint}): {Status}",
                    job.Id, job.OvidFingerprint, status);

                return new SubmitResult
                {
                    Success = true,
                    JobId = job.Id,
                    Title = job.Title,
                    Message = status switch
                    {
                        "registered" => "Fingerprint registered",
                        "already_exists" => "Already in OVID database",
                        _ => message
                    },
                    Status = status
                };
            }

            Logger.LogWarning("OVID submission failed for job {JobId}: {Message}", job.Id, message);
            return new SubmitResult { Success = false, JobId = job.Id, Title = job.Title, Message = message, Status = "failed" };
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error submitting OVID fingerprint for job {JobId}", job.Id);
            return new SubmitResult { Success = false, JobId = job.Id, Title = job.Title, Message = ex.Message, Status = "failed" };
        }
    }

    protected override async Task<List<Job>> GetPendingJobsAsync(CancellationToken ct)
    {
        return await Db.Jobs
            .Where(j => !string.IsNullOrEmpty(j.OvidFingerprint) &&
                        (j.HasNiceTitle || !string.IsNullOrEmpty(j.TitleManual)) &&
                        !j.OvidSubmitted)
            .ToListAsync(ct);
    }

    protected override string GetServiceName() => "OvidSubmitService";
}
