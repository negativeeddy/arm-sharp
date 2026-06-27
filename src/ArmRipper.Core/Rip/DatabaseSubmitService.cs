using System.Text.Json;
using ArmRipper.Core.Configuration;
using ArmRipper.Core.Infrastructure.Data;
using ArmRipper.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArmRipper.Core.Rip;

public sealed class DatabaseSubmitService(
    ArmDbContext db,
    IHttpClientFactory httpClientFactory,
    IOptions<ArmSettings> settings,
    ILoggerFactory loggerFactory) : IDatabaseSubmitService
{
    private readonly ILogger logger = loggerFactory.CreateLogger("DatabaseSubmitService");

    private const string ApiBaseUrl = "https://1337server.pythonanywhere.com";

    public async Task<DatabaseSubmitResult> SubmitJobAsync(Job job, CancellationToken ct = default)
    {
        // Skip if already submitted
        if (job.IsStageComplete(RipStage.CrcSubmitted))
        {
            logger.LogInformation("Job {JobId} already submitted, skipping", job.Id);
            return new DatabaseSubmitResult { Success = true, JobId = job.Id, Message = "Already submitted", Status = "skipped" };
        }

        // Validate we have what we need
        if (string.IsNullOrWhiteSpace(job.CrcId))
        {
            var msg = $"Job {job.Id} has no CRC64 hash, cannot submit";
            logger.LogWarning(msg);
            return new DatabaseSubmitResult { Success = false, JobId = job.Id, Message = msg, Status = "failed" };
        }

        if (!job.HasNiceTitle && string.IsNullOrWhiteSpace(job.Title) && string.IsNullOrWhiteSpace(job.TitleManual))
        {
            var msg = $"Job {job.Id} has no nice title, cannot submit";
            logger.LogWarning(msg);
            return new DatabaseSubmitResult { Success = false, JobId = job.Id, Message = msg, Status = "failed" };
        }

        var apiKey = job.Config?.ArmApiKey ?? settings.Value.ArmApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            var msg = "No ARM API key configured. Set ArmApiKey in settings.";
            logger.LogWarning(msg);
            return new DatabaseSubmitResult { Success = false, JobId = job.Id, Message = msg, Status = "failed" };
        }

        try
        {
            var url = $"{ApiBaseUrl}/api/v1/?mode=p" +
                      $"&api_key={Uri.EscapeDataString(apiKey)}" +
                      $"&crc64={Uri.EscapeDataString(job.CrcId)}" +
                      $"&t={Uri.EscapeDataString(job.Title ?? "")}" +
                      $"&y={Uri.EscapeDataString(job.Year ?? "")}" +
                      $"&imdb={Uri.EscapeDataString(job.ImdbId ?? "")}" +
                      $"&hnt={job.HasNiceTitle}" +
                      $"&l={Uri.EscapeDataString(job.Label ?? "")}" +
                      $"&vt={Uri.EscapeDataString(job.VideoType ?? "")}";

            var httpClient = httpClientFactory.CreateClient("DatabaseSubmitService");
            var response = await httpClient.GetStringAsync(url, ct);
            var result = JsonSerializer.Deserialize<SubmitApiResponse>(response);

            if (result?.Success == true)
            {
                job.MarkStageComplete(RipStage.CrcSubmitted);
                await db.SaveChangesAsync(ct);
                logger.LogInformation("Successfully submitted CRC64 for job {JobId} ({Title})", job.Id, job.Title);
                return new DatabaseSubmitResult { Success = true, JobId = job.Id, Message = "Submitted", Status = "added" };
            }

            var errorMsg = result?.Error ?? "Unknown error from remote API";

            // If the remote says the CRC already exists, treat it as submitted
            // so we never retry.
            if (errorMsg.Contains("already", StringComparison.OrdinalIgnoreCase) ||
                errorMsg.Contains("exist", StringComparison.OrdinalIgnoreCase) ||
                errorMsg.Contains("duplicate", StringComparison.OrdinalIgnoreCase))
            {
                job.MarkStageComplete(RipStage.CrcSubmitted);
                await db.SaveChangesAsync(ct);
                logger.LogInformation("CRC64 for job {JobId} already exists remotely, marking as submitted", job.Id);
                return new DatabaseSubmitResult { Success = true, JobId = job.Id, Message = "Already exists remotely", Status = "already_exists" };
            }

            logger.LogWarning("Failed to submit CRC64 for job {JobId}: {Error}", job.Id, errorMsg);
            return new DatabaseSubmitResult { Success = false, JobId = job.Id, Message = errorMsg, Status = "failed" };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error submitting CRC64 for job {JobId}", job.Id);
            return new DatabaseSubmitResult { Success = false, JobId = job.Id, Message = ex.Message, Status = "failed" };
        }
    }

    public async Task<List<DatabaseSubmitResult>> SubmitPendingAsync(CancellationToken ct = default)
    {
        var results = new List<DatabaseSubmitResult>();

        var pendingJobs = await db.Jobs
            .Include(j => j.Config)
            .Where(j => j.DiscType == DiscType.Dvd &&
                        !string.IsNullOrEmpty(j.CrcId) &&
                        (j.HasNiceTitle || !string.IsNullOrEmpty(j.TitleManual)))
            .ToListAsync(ct);

        // Filter out already-submitted in memory (since CompletedStages is not queryable via EF)
        var toSubmit = pendingJobs
            .Where(j => !j.IsStageComplete(RipStage.CrcSubmitted))
            .ToList();

        logger.LogInformation("Found {Total} DVD jobs with CRC64, {Pending} pending submission",
            pendingJobs.Count, toSubmit.Count);

        foreach (var job in toSubmit)
        {
            ct.ThrowIfCancellationRequested();
            var result = await SubmitJobAsync(job, ct);
            results.Add(result);
        }

        return results;
    }

    private record SubmitApiResponse
    {
        public bool Success { get; init; }
        public string? Error { get; init; }
    }
}
