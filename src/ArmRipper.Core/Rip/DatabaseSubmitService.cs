using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
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
    ILoggerFactory loggerFactory)
    : SubmitServiceBase(db, loggerFactory.CreateLogger("DatabaseSubmitService")), IDatabaseSubmitService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private const string ApiBaseUrl = "https://1337server.pythonanywhere.com";
    private const string ApiEndpoint = "/api/v1/";

    private static readonly Regex ApiKeyRedactionRegex = new(
        @"(api_key=)[^&]*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));

    public override async Task<SubmitResult> SubmitJobAsync(Job job, CancellationToken ct = default)
    {
        // Skip if already submitted
        if (job.IsStageComplete(RipStage.CrcSubmitted))
        {
            Logger.LogInformation("Job {JobId} already submitted, skipping", job.Id);
            return new SubmitResult { Success = true, JobId = job.Id, Title = job.Title, Message = "Already submitted", Status = "skipped" };
        }

        // Validate we have what we need
        if (string.IsNullOrWhiteSpace(job.CrcId))
        {
            var msg = $"Job {job.Id} has no CRC64 hash, cannot submit";
            Logger.LogWarning(msg);
            return new SubmitResult { Success = false, JobId = job.Id, Title = job.Title, Message = msg, Status = "failed" };
        }

        if (!job.HasNiceTitle && string.IsNullOrWhiteSpace(job.Title) && string.IsNullOrWhiteSpace(job.TitleManual))
        {
            var msg = $"Job {job.Id} has no nice title, cannot submit";
            Logger.LogWarning(msg);
            return new SubmitResult { Success = false, JobId = job.Id, Title = job.Title, Message = msg, Status = "failed" };
        }

        var apiKey = job.Config?.ArmApiKey ?? settings.Value.ArmApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            var msg = "No ARM API key configured. Set ArmApiKey in settings.";
            Logger.LogWarning(msg);
            return new SubmitResult { Success = false, JobId = job.Id, Title = job.Title, Message = msg, Status = "failed" };
        }

        try
        {
            var videoTypeParam = job.VideoType == VideoContentType.Unknown
                ? ""
                : job.VideoType.ToString().ToLowerInvariant();

            var httpClient = httpClientFactory.CreateClient("DatabaseSubmitService");

            // Build form content without putting api_key in URL
            var formContent = new List<KeyValuePair<string, string>>
            {
                new("mode", "p"),
                new("api_key", apiKey),
                new("crc64", job.CrcId ?? string.Empty),
                new("t", job.Title ?? string.Empty),
                new("y", job.Year ?? string.Empty),
                new("imdb", job.ImdbId ?? string.Empty),
                new("hnt", job.HasNiceTitle.ToString().ToLowerInvariant()),
                new("l", job.Label ?? string.Empty),
                new("vt", videoTypeParam),
            };

            var content = new FormUrlEncodedContent(formContent);

            // Try to put API key in headers as well for compatibility
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiBaseUrl}{ApiEndpoint}");
            request.Content = content;
            request.Headers.TryAddWithoutValidation("X-API-Key", apiKey);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            request.Headers.TryAddWithoutValidation("X-Arm-Api-Key", apiKey);

            Logger.LogDebug("Submitting CRC64 for job {JobId} via POST to {BaseUrl}{Endpoint} (api_key in body/headers)", job.Id, ApiBaseUrl, ApiEndpoint);

            var response = await httpClient.SendAsync(request, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);

            Logger.LogDebug("Raw API response for job {JobId}: {Response}", job.Id, responseBody);

            // Parse the response. The API has two shapes:
            //   Success: {"mode":"post", "results":{"crc_id":"...","date_added":"...",...}}
            //   Failure: {"mode":"post", "error":"message"}
            SubmitApiResponse? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<SubmitApiResponse>(responseBody, JsonOptions);
            }
            catch (JsonException ex)
            {
                Logger.LogWarning(ex, "Failed to parse API response for job {JobId}: {Response}", job.Id, responseBody);
                return new SubmitResult
                {
                    Success = false, JobId = job.Id, Title = job.Title,
                    Message = $"Unparseable response: {responseBody}",
                    Status = "failed"
                };
            }

            if (parsed is null)
            {
                return new SubmitResult
                {
                    Success = false, JobId = job.Id, Title = job.Title,
                    Message = $"Null response from API",
                    Status = "failed"
                };
            }

            // ── Success: results object is present ──
            if (parsed.Results is not null)
            {
                await Db.MarkStageCompleteAsync(job, RipStage.CrcSubmitted, ct);
                Logger.LogInformation(
                    "Successfully submitted CRC64 for job {JobId} ({Title}) — crc_id={CrcId}",
                    job.Id, job.Title, parsed.Results.CrcId);
                return new SubmitResult { Success = true, JobId = job.Id, Title = job.Title, Message = "Submitted", Status = "added" };
            }

            // ── Failure: error field is present ──
            var errorMsg = parsed.Error ?? responseBody;

            // If the remote says the CRC already exists, treat it as submitted
            // so we never retry.
            if (errorMsg.Contains("already", StringComparison.OrdinalIgnoreCase) ||
                errorMsg.Contains("exist", StringComparison.OrdinalIgnoreCase) ||
                errorMsg.Contains("duplicate", StringComparison.OrdinalIgnoreCase))
            {
                await Db.MarkStageCompleteAsync(job, RipStage.CrcSubmitted, ct);
                Logger.LogInformation("CRC64 for job {JobId} already exists remotely, marking as submitted", job.Id);
                return new SubmitResult { Success = true, JobId = job.Id, Title = job.Title, Message = "Already exists remotely", Status = "already_exists" };
            }

            Logger.LogWarning("Failed to submit CRC64 for job {JobId}: {Error}", job.Id, errorMsg);
            return new SubmitResult { Success = false, JobId = job.Id, Title = job.Title, Message = errorMsg, Status = "failed" };
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error submitting CRC64 for job {JobId}", job.Id);
            return new SubmitResult { Success = false, JobId = job.Id, Title = job.Title, Message = ex.Message, Status = "failed" };
        }
    }

    protected override async Task<List<Job>> GetPendingJobsAsync(CancellationToken ct)
    {
        var pendingJobs = await Db.Jobs
            .Include(j => j.Config)
            .Where(j => j.DiscType == DiscType.Dvd &&
                        !string.IsNullOrEmpty(j.CrcId) &&
                        (j.HasNiceTitle || !string.IsNullOrEmpty(j.TitleManual)))
            .ToListAsync(ct);

        // Filter out already-submitted in memory — CompletedStages is a pipe-delimited
        // string that IsStageComplete splits client-side, so the filter can't be
        // translated to SQL.
        return pendingJobs
            .Where(j => !j.IsStageComplete(RipStage.CrcSubmitted))
            .ToList();
    }

    protected override string GetServiceName() => "DatabaseSubmitService";

    private static string RedactApiKey(string url) => ApiKeyRedactionRegex.Replace(url, "$1<redacted>");

    /// <summary>
    /// Models the JSON response from the ARM online database API.
    /// The actual wire format is:
    /// <code>
    ///   Success — {"mode":"post", "results":{"crc_id":"...", "date_added":"...", "title":"...", ...}}
    ///   Failure — {"mode":"post", "error":"..."}
    /// </code>
    /// </summary>
    private sealed record SubmitApiResponse
    {
        public string? Mode { get; init; }
        public SubmitApiResults? Results { get; init; }
        public string? Error { get; init; }
    }

    private sealed record SubmitApiResults
    {
        public string? CrcId { get; init; }
        public string? DateAdded { get; init; }
        public string? Disctype { get; init; }
        public string? Hasnicetitle { get; init; }
        public string? Imdb { get; init; }
        public string? Title { get; init; }
        public string? Year { get; init; }
    }
}
