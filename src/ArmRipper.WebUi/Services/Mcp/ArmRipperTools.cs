using System.ComponentModel;
using System.Text.Json;
using ArmRipper.Core.Configuration;
using ArmRipper.Core.Infrastructure.Data;
using ArmRipper.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ArmRipper.WebUi.Services.Mcp;

/// <summary>
/// MCP tools that expose ARM Sharp internals for AI agent observation and diagnosis.
/// </summary>
[ModelContextProtocol.Server.McpServerToolType]
public class ArmRipperTools
{
    private readonly ArmDbContext _db;
    private readonly IOptions<ArmSettings> _settings;

    public ArmRipperTools(ArmDbContext db, IOptions<ArmSettings> settings)
    {
        _db = db;
        _settings = settings;
    }

    /// <summary>
    /// Returns a paginated list of rip jobs, with optional status filtering.
    /// </summary>
    [ModelContextProtocol.Server.McpServerTool]
    [Description("Returns a paginated list of rip jobs, with optional status filtering.")]
    public async Task<string> GetJobs(
        [Description("Filter by job status (e.g. 'Active', 'Success', 'Failure', 'Cancelled'). Omit for all.")]
            string? status = null,
        [Description("Number of records to skip (for pagination). Default: 0.")]
            int offset = 0,
        [Description("Maximum records to return. Default: 20, max: 100.")]
            int limit = 20)
    {
        limit = Math.Clamp(limit, 1, 100);
        offset = Math.Max(0, offset);

        var query = _db.Jobs
            .Include(j => j.Config)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(status))
        {
            if (Enum.TryParse<JobState>(status, ignoreCase: true, out var parsed))
                query = query.Where(j => j.Status == parsed);
        }

        var totalCount = await query.CountAsync();

        var jobs = await query
            .OrderByDescending(j => j.StartTime)
            .Skip(offset)
            .Take(limit)
            .Select(j => new
            {
                j.Id,
                j.Title,
                j.Year,
                j.VideoType,
                Status = j.Status.ToString(),
                Stage = j.Stage != null ? j.Stage.ToString() : null,
                j.StartTime,
                j.StopTime,
                j.DiscType,
                j.Label,
                j.DevPath,
                j.Errors,
                j.Warnings,
                j.CompletedStages,
                HasConfig = j.Config != null
            })
            .ToListAsync();

        var result = new
        {
            totalCount,
            offset,
            limit,
            jobs
        };

        return JsonSerializer.Serialize(result, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
    }

    /// <summary>
    /// Reads a job's log file with offset-based pagination for efficient browsing of long logs.
    /// </summary>
    [ModelContextProtocol.Server.McpServerTool]
    [Description("Reads a job's log file with offset-based pagination for efficient browsing of long logs.")]
    public async Task<string> GetLogs(
        [Description("The job ID to read logs for.")]
            int jobId,
        [Description("Line number to start reading from (0-based). Default: 0.")]
            int offset = 0,
        [Description("Number of lines to return. Default: 50, max: 500.")]
            int pageSize = 50)
    {
        pageSize = Math.Clamp(pageSize, 1, 500);
        offset = Math.Max(0, offset);

        var job = await _db.Jobs
            .Include(j => j.Config)
            .FirstOrDefaultAsync(j => j.Id == jobId);

        if (job is null)
            return JsonSerializer.Serialize(new { error = $"Job {jobId} not found." });

        var logPath = Path.Combine(
            job.Config?.LogPath ?? ArmPaths.GetLogPath(_settings.Value),
            job.LogFile ?? $"{jobId}.log");

        if (!File.Exists(logPath))
            return JsonSerializer.Serialize(new { error = $"Log file not found: {logPath}" });

        try
        {
            var allLines = await File.ReadAllLinesAsync(logPath);
            var totalLines = allLines.Length;

            // Clamp offset to valid range
            if (offset >= totalLines)
            {
                return JsonSerializer.Serialize(new
                {
                    jobId,
                    totalLines,
                    offset,
                    pageSize,
                    lines = Array.Empty<string>(),
                    startLine = totalLines,
                    endLine = totalLines,
                    hasMore = false
                }, new JsonSerializerOptions { WriteIndented = true });
            }

            var endLine = Math.Min(offset + pageSize, totalLines);
            var lines = allLines[offset..endLine];
            var hasMore = endLine < totalLines;

            var result = new
            {
                jobId,
                totalLines,
                offset,
                pageSize,
                lines,
                startLine = offset,
                endLine,
                hasMore
            };

            return JsonSerializer.Serialize(result, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = $"Error reading log: {ex.Message}" });
        }
    }

    /// <summary>
    /// Returns the current ARM Sharp configuration settings.
    /// </summary>
    [ModelContextProtocol.Server.McpServerTool]
    [Description("Returns the current ARM Sharp configuration settings.")]
    public Task<string> GetConfig()
    {
        var settings = _settings.Value;

        var config = new
        {
            // Paths
            settings.RawPath,
            settings.TranscodePath,
            settings.CompletedPath,
            settings.LogPath,
            settings.DbFile,

            // Processing
            settings.SkipTranscode,
            settings.MainFeature,
            settings.UseFfmpeg,
            settings.ManualWait,
            settings.ManualWaitTime,
            settings.AllowDuplicates,
            settings.PreferWidescreen,
            settings.Prevent99,
            settings.GetVideoTitle,
            settings.GetAudioTitle,
            settings.AutoEject,
            settings.DelRawFiles,
            settings.RipMethod,
            settings.MinLength,
            settings.MaxLength,
            settings.DestExt,

            // HandBrake
            HasHbPresetDvd = !string.IsNullOrEmpty(settings.HbPresetDvd),
            HasHbPresetBd = !string.IsNullOrEmpty(settings.HbPresetBd),
            HasHbArgsDvd = !string.IsNullOrEmpty(settings.HbArgsDvd),
            HasHbArgsBd = !string.IsNullOrEmpty(settings.HbArgsBd),

            // FFmpeg
            settings.FfmpegCli,
            HasFfmpegPostFileArgs = !string.IsNullOrEmpty(settings.FfmpegPostFileArgs),

            // Notifications
            settings.NotifyRip,
            settings.NotifyTranscode,
            HasPbKey = !string.IsNullOrEmpty(settings.PbKey),
            HasIftttKey = !string.IsNullOrEmpty(settings.IftttKey),
            HasPoUserKey = !string.IsNullOrEmpty(settings.PoUserKey),
            HasBashScript = !string.IsNullOrEmpty(settings.BashScript),
            HasJsonUrl = !string.IsNullOrEmpty(settings.JsonUrl),
            HasApprise = !string.IsNullOrEmpty(settings.Apprise),

            // API Keys (show only whether they're set, not the values)
            HasArmApiKey = !string.IsNullOrEmpty(settings.ArmApiKey),
            HasOmdbApiKey = !string.IsNullOrEmpty(settings.OmdbApiKey),
            HasTmdbApiKey = !string.IsNullOrEmpty(settings.TmdbApiKey),
            HasTvdbApiKey = !string.IsNullOrEmpty(settings.TvdbApiKey),
            HasOvidApiToken = !string.IsNullOrEmpty(settings.OvidApiToken),
            settings.MetadataProvider,

            // Server
            settings.WebServerIp,
            settings.WebServerPort,
            settings.DisableLogin,

            // Disc polling
            settings.DiscPollingEnabled,
            settings.DiscPollIntervalSeconds,
            settings.EjectCooldownSeconds,

            // Disc DB
            settings.DiscDbEnabled,
            settings.DiscDbApiBaseUrl,
            settings.DiscDbMinConfidence,
            settings.DiscDbRequireConfirmation,

            // OVID
            settings.OvidSubmitEnabled,

            // Permissions
            settings.SetMediaPermissions,
            settings.ChmodValue,
            settings.SetMediaOwner,
            settings.ChownUser,
            settings.ChownGroup,

            // Emby
            settings.EmbyRefresh,
            settings.EmbyServer,
            settings.EmbyPort,
            HasEmbyApiKey = !string.IsNullOrEmpty(settings.EmbyApiKey),

            // Concurrency
            settings.MaxConcurrentTranscodes,
            settings.MaxConcurrentMakemkvInfo,

            // Misc
            settings.TestMode,
            settings.MakeMkvPermaKey,
            settings.FileBotNonStrict
        };

        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        return Task.FromResult(json);
    }
}
