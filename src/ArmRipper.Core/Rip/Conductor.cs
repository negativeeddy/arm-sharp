using ArmRipper.Core.Configuration;
using ArmRipper.Core.Infrastructure;
using ArmRipper.Core.Infrastructure.Data;
using ArmRipper.Core.Models;
using ArmRipper.Core.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArmRipper.Core.Rip;

public sealed class Conductor(
    ILoggerFactory loggerFactory,
    ArmDbContext db,
    ICliProcessRunner runner,
    IOptions<ArmSettings> settings,
    IIdentifyService identifyService,
    IArmRipperService armRipperService,
    IMusicBrainzService musicBrainzService,
    NotificationService notificationService,
    IEnumerable<INotificationBroadcaster> broadcasters,
    JobFileLoggerProvider fileLogProvider) : IConductor
{
    private readonly ILogger logger = loggerFactory.CreateLogger(nameof(Conductor));
    /// <summary>Broadcast job state to all connected UI clients with error handling.</summary>
    private async Task BroadcastJobUpdateAsync(Job job)
    {
        var update = JobUpdate.FromJob(job);
        foreach (var b in broadcasters)
        {
            try
            {
                await b.BroadcastJobUpdateAsync(update);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Broadcast failed for job {JobId}", job.Id);
            }
        }
    }

    public async Task<int> RunAsync(string devicePath, CancellationToken ct = default)
    {
        Job? job = null;
        try
        {
            Setup();
            job = await SetupJobAsync(devicePath, ct);
            return await ProcessJobAsync(job, ct);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Job {JobId} cancelled (token)", job?.Id);
            if (job is not null)
            {
                try
                {
                    // Only override with Stopping if not already terminal
                    if (!job.Status.IsTerminal() && job.Status != JobState.Stopping)
                    {
                        job.Status = JobState.Stopping;
                        job.StopTime ??= DateTime.UtcNow;
                        job.ProgressMessage = "Cancelled — can be resumed";
                        await db.SaveChangesAsync(CancellationToken.None);
                        await BroadcastJobUpdateAsync(job);
                    }
                }
                catch { /* best effort */ }
            }
            return 1;
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "A fatal error has occurred and ARM is exiting");
            if (job is not null && job.Status != JobState.Stopping)
            {
                job.Status = JobState.Failure;
                job.Errors = ex.Message;
                job.ProgressMessage = null;
                try { await db.SaveChangesAsync(CancellationToken.None); } catch { /* best effort */ }
                await BroadcastJobUpdateAsync(job);
            }
            return 1;
        }
    }

    /// <summary>
    /// Creates a new forked job from an existing job and starts from the transcode stage.
    /// The new job reuses the original's metadata (title, year, video type) and config,
    /// but skips the identify and rip stages — it jumps straight to transcoding the raw file(s).
    /// </summary>
    /// <param name="originalJobId">The ID of the original job to fork from.</param>
    /// <param name="rawFilePath">Path to the raw .mkv file (or its parent directory) to transcode.</param>
    public async Task<int> RunForkedTranscodeAsync(int originalJobId, string rawFilePath, CancellationToken ct = default)
    {
        // ── 1. Load the original job ──
        var originalJob = await db.Jobs
            .Include(j => j.Config)
            .FirstOrDefaultAsync(j => j.Id == originalJobId, ct);

        if (originalJob is null)
        {
            logger.LogError("Original job {JobId} not found — cannot fork transcode", originalJobId);
            return 1;
        }

        // Determine the raw directory — if a specific file was given, transcode its directory
        var rawDir = File.Exists(rawFilePath)
            ? Path.GetDirectoryName(rawFilePath)!
            : rawFilePath;

        if (!Directory.Exists(rawDir))
        {
            logger.LogError("Raw directory {RawDir} does not exist", rawDir);
            return 1;
        }

        // ── 2. Create the forked job ──
        var job = new Job
        {
            DevPath = rawDir,
            Status = JobState.Active,
            StartTime = DateTime.UtcNow,
            OriginalJobId = originalJob.Id,
            Title = originalJob.Title,
            TitleAuto = originalJob.TitleAuto,
            Year = originalJob.Year,
            YearAuto = originalJob.YearAuto,
            VideoType = originalJob.VideoType,
            VideoTypeAuto = originalJob.VideoTypeAuto,
            DiscType = originalJob.DiscType,
            ImdbId = originalJob.ImdbId,
            ImdbIdAuto = originalJob.ImdbIdAuto,
            PosterUrl = originalJob.PosterUrl,
            PosterUrlAuto = originalJob.PosterUrlAuto,
            Label = originalJob.Label,
            ManualStart = true
        };

        db.Jobs.Add(job);
        await db.SaveChangesAsync(ct);

        job.LogFile = $"{job.Id}.log";
        job.TransitionToStage(RipStage.Setup);

        // ── 3. Copy the config snapshot from the original job's config or fall back to settings ──
        var armSettings = settings.Value;
        var sourceConfig = originalJob.Config;

        var config = new ConfigSnapshot
        {
            JobId = job.Id,
            SkipTranscode = sourceConfig?.SkipTranscode ?? armSettings.SkipTranscode,
            MainFeature = sourceConfig?.MainFeature ?? armSettings.MainFeature,
            UseFfmpeg = sourceConfig?.UseFfmpeg ?? armSettings.UseFfmpeg,
            ManualWait = sourceConfig?.ManualWait ?? armSettings.ManualWait,
            ManualWaitTime = sourceConfig?.ManualWaitTime ?? armSettings.ManualWaitTime,
            AllowDuplicates = sourceConfig?.AllowDuplicates ?? armSettings.AllowDuplicates,
            Prevent99 = sourceConfig?.Prevent99 ?? armSettings.Prevent99,
            GetVideoTitle = sourceConfig?.GetVideoTitle ?? armSettings.GetVideoTitle,
            GetAudioTitle = sourceConfig?.GetAudioTitle ?? armSettings.GetAudioTitle,
            AutoEject = false, // Don't eject — no physical disc
            DelRawFiles = sourceConfig?.DelRawFiles ?? armSettings.DelRawFiles,
            RawPath = sourceConfig?.RawPath ?? armSettings.RawPath,
            TranscodePath = sourceConfig?.TranscodePath ?? armSettings.TranscodePath,
            CompletedPath = sourceConfig?.CompletedPath ?? armSettings.CompletedPath,
            LogPath = sourceConfig?.LogPath ?? armSettings.LogPath,
            RipMethod = sourceConfig?.RipMethod ?? armSettings.RipMethod,
            MinLength = sourceConfig?.MinLength ?? armSettings.MinLength,
            MaxLength = sourceConfig?.MaxLength ?? armSettings.MaxLength,
            HbPresetDvd = sourceConfig?.HbPresetDvd ?? armSettings.HbPresetDvd,
            HbPresetBd = sourceConfig?.HbPresetBd ?? armSettings.HbPresetBd,
            HbArgsDvd = sourceConfig?.HbArgsDvd ?? armSettings.HbArgsDvd,
            HbArgsBd = sourceConfig?.HbArgsBd ?? armSettings.HbArgsBd,
            DestExt = sourceConfig?.DestExt ?? armSettings.DestExt,
            FfmpegCli = sourceConfig?.FfmpegCli ?? armSettings.FfmpegCli,
            FfmpegPreFileArgs = sourceConfig?.FfmpegPreFileArgs ?? armSettings.FfmpegPreFileArgs,
            FfmpegPostFileArgs = sourceConfig?.FfmpegPostFileArgs ?? armSettings.FfmpegPostFileArgs,
            MkvArgs = sourceConfig?.MkvArgs ?? armSettings.MkvArgs,
            ExtrasSub = sourceConfig?.ExtrasSub ?? armSettings.ExtrasSub,
            InstallPath = sourceConfig?.InstallPath ?? armSettings.InstallPath,
            DbFile = sourceConfig?.DbFile ?? armSettings.DbFile,
            NotifyRip = false, // Skip rip notifications
            NotifyTranscode = sourceConfig?.NotifyTranscode ?? armSettings.NotifyTranscode,
            PbKey = sourceConfig?.PbKey ?? armSettings.PbKey,
            IftttKey = sourceConfig?.IftttKey ?? armSettings.IftttKey,
            PoUserKey = sourceConfig?.PoUserKey ?? armSettings.PoUserKey,
            BashScript = sourceConfig?.BashScript ?? armSettings.BashScript,
            JsonUrl = sourceConfig?.JsonUrl ?? armSettings.JsonUrl,
            Apprise = sourceConfig?.Apprise ?? armSettings.Apprise,
            OmdbApiKey = sourceConfig?.OmdbApiKey ?? armSettings.OmdbApiKey,
            TmdbApiKey = sourceConfig?.TmdbApiKey ?? armSettings.TmdbApiKey,
            ArmApiKey = sourceConfig?.ArmApiKey ?? armSettings.ArmApiKey,
            MetadataProvider = sourceConfig?.MetadataProvider ?? armSettings.MetadataProvider,
            WebServerPort = sourceConfig?.WebServerPort ?? armSettings.WebServerPort,
            WebServerIp = sourceConfig?.WebServerIp ?? armSettings.WebServerIp,
            UiBaseUrl = sourceConfig?.UiBaseUrl ?? armSettings.UiBaseUrl,
            EmbyRefresh = sourceConfig?.EmbyRefresh ?? armSettings.EmbyRefresh,
            EmbyServer = sourceConfig?.EmbyServer ?? armSettings.EmbyServer,
            EmbyPort = sourceConfig?.EmbyPort ?? armSettings.EmbyPort,
            EmbyApiKey = sourceConfig?.EmbyApiKey ?? armSettings.EmbyApiKey,
            MaxConcurrentTranscodes = sourceConfig?.MaxConcurrentTranscodes ?? armSettings.MaxConcurrentTranscodes,
            MaxConcurrentMakemkvInfo = sourceConfig?.MaxConcurrentMakemkvInfo ?? armSettings.MaxConcurrentMakemkvInfo,
            DiscDbEnabled = sourceConfig?.DiscDbEnabled ?? armSettings.DiscDbEnabled,
            DiscDbApiBaseUrl = sourceConfig?.DiscDbApiBaseUrl ?? armSettings.DiscDbApiBaseUrl,
            DiscDbMinConfidence = sourceConfig?.DiscDbMinConfidence ?? armSettings.DiscDbMinConfidence,
            DiscDbRequireConfirmation = sourceConfig?.DiscDbRequireConfirmation ?? armSettings.DiscDbRequireConfirmation
        };

        db.ConfigSnapshots.Add(config);
        job.MarkStageComplete(RipStage.Setup);
        job.MarkStageComplete(RipStage.Identify);
        job.MarkStageComplete(RipStage.Rip);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Forked job {JobId} created from original job {OriginalJobId} for raw directory {RawDir}",
            job.Id, originalJob.Id, rawDir);

        // ── 4. Set up file logger and run ──
        using var _ = logger.BeginScope(new Dictionary<string, object>
        {
            [JobFileLoggerProvider.LogFilePathKey] = job.GetLogFilePath()
        });

        try
        {
            logger.LogInformation("************* Starting forked transcode *************");
            logger.LogInformation("Original job: {OriginalJobId} ({Title})", originalJob.Id, originalJob.Title);
            logger.LogInformation("Raw directory: {RawDir}", rawDir);

            // Call directly into the rip service — it will skip MakeMKV (rip complete)
            // and proceed to transcode, finalize, and cleanup.
            var directory = await armRipperService.RipVisualMediaAsync(job, job.LogFile ?? "", false, false, ct);
            job.Path = directory;

            if (job.Status is not JobState.Failure)
                job.Status = JobState.Success;
            job.StopTime = DateTime.UtcNow;
            if (job.StartTime != default)
            {
                var jobLength = job.StopTime.Value - job.StartTime;
                job.JobLength = $"{(int)jobLength.TotalHours}:{jobLength.Minutes:D2}:{jobLength.Seconds:D2}";
            }

            await db.SaveChangesAsync(ct);
            await BroadcastJobUpdateAsync(job);
            logger.LogInformation("************* Forked transcode complete *************");
            return 0;
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Forked transcode failed for job {JobId}", job.Id);
            job.Status = JobState.Failure;
            job.Errors = ex.Message;
            job.ProgressMessage = null;
            try { await db.SaveChangesAsync(ct); } catch { /* best effort */ }
            await BroadcastJobUpdateAsync(job);
            return 1;
        }
    }

    /// <summary>
    /// Creates a new standalone job from raw MKV files that were ripped elsewhere,
    /// skipping identify and rip stages — jumps straight to transcoding.
    /// </summary>
    public async Task<Job> CreateImportJobAsync(string rawFilePath, string title, string? year, string? videoType, string? discType, CancellationToken ct = default)
    {
        // ── 1. Determine the raw directory ──
        var rawDir = File.Exists(rawFilePath)
            ? Path.GetDirectoryName(rawFilePath)!
            : rawFilePath;

        if (!Directory.Exists(rawDir))
        {
            throw new DirectoryNotFoundException($"Raw directory does not exist: {rawDir}");
        }

        // ── 2. Parse disc type ──
        var parsedDiscType = (discType ?? "").ToLowerInvariant() switch
        {
            "dvd" => DiscType.Dvd,
            "bluray" or "bd" or "blu-ray" => DiscType.Bluray,
            "uhd" or "4k" => DiscType.Uhd,
            _ => DiscType.Bluray // safest default for imported MKVs
        };

        // ── 3. Create the job with user-provided metadata ──
        var armSettings = settings.Value;
        var job = new Job
        {
            DevPath = rawDir,
            Status = JobState.Active,
            StartTime = DateTime.UtcNow,
            Title = title,
            TitleAuto = title,
            Year = year ?? "0000",
            YearAuto = year ?? "0000",
            VideoType = videoType ?? "movie",
            VideoTypeAuto = videoType ?? "movie",
            DiscType = parsedDiscType,
            Label = title,
            ManualStart = true
        };

        db.Jobs.Add(job);
        await db.SaveChangesAsync(ct);

        job.LogFile = $"{job.Id}.log";
        job.TransitionToStage(RipStage.Setup);

        // ── 4. Create config snapshot from current settings ──
        var config = new ConfigSnapshot
        {
            JobId = job.Id,
            SkipTranscode = armSettings.SkipTranscode,
            MainFeature = armSettings.MainFeature,
            UseFfmpeg = armSettings.UseFfmpeg,
            ManualWait = false,
            AllowDuplicates = armSettings.AllowDuplicates,
            Prevent99 = armSettings.Prevent99,
            GetVideoTitle = false,
            GetAudioTitle = armSettings.GetAudioTitle,
            AutoEject = false,
            DelRawFiles = false, // Never auto-delete imported raw files
            RawPath = armSettings.RawPath,
            TranscodePath = armSettings.TranscodePath,
            CompletedPath = armSettings.CompletedPath,
            LogPath = armSettings.LogPath,
            RipMethod = armSettings.RipMethod,
            MinLength = armSettings.MinLength,
            MaxLength = armSettings.MaxLength,
            HbPresetDvd = armSettings.HbPresetDvd,
            HbPresetBd = armSettings.HbPresetBd,
            HbArgsDvd = armSettings.HbArgsDvd,
            HbArgsBd = armSettings.HbArgsBd,
            DestExt = armSettings.DestExt,
            FfmpegCli = armSettings.FfmpegCli,
            FfmpegPreFileArgs = armSettings.FfmpegPreFileArgs,
            FfmpegPostFileArgs = armSettings.FfmpegPostFileArgs,
            MkvArgs = armSettings.MkvArgs,
            ExtrasSub = armSettings.ExtrasSub,
            InstallPath = armSettings.InstallPath,
            DbFile = armSettings.DbFile,
            NotifyRip = false,
            NotifyTranscode = armSettings.NotifyTranscode,
            PbKey = armSettings.PbKey,
            IftttKey = armSettings.IftttKey,
            PoUserKey = armSettings.PoUserKey,
            BashScript = armSettings.BashScript,
            JsonUrl = armSettings.JsonUrl,
            Apprise = armSettings.Apprise,
            OmdbApiKey = armSettings.OmdbApiKey,
            TmdbApiKey = armSettings.TmdbApiKey,
            ArmApiKey = armSettings.ArmApiKey,
            MetadataProvider = armSettings.MetadataProvider,
            WebServerPort = armSettings.WebServerPort,
            WebServerIp = armSettings.WebServerIp,
            UiBaseUrl = armSettings.UiBaseUrl,
            EmbyRefresh = armSettings.EmbyRefresh,
            EmbyServer = armSettings.EmbyServer,
            EmbyPort = armSettings.EmbyPort,
            EmbyApiKey = armSettings.EmbyApiKey,

            MaxConcurrentTranscodes = armSettings.MaxConcurrentTranscodes,
            MaxConcurrentMakemkvInfo = armSettings.MaxConcurrentMakemkvInfo,
            DiscDbEnabled = armSettings.DiscDbEnabled,
            DiscDbApiBaseUrl = armSettings.DiscDbApiBaseUrl,
            DiscDbMinConfidence = armSettings.DiscDbMinConfidence,
            DiscDbRequireConfirmation = armSettings.DiscDbRequireConfirmation
        };

        db.ConfigSnapshots.Add(config);
        job.MarkStageComplete(RipStage.Setup);
        job.MarkStageComplete(RipStage.Identify);
        job.MarkStageComplete(RipStage.Rip);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Import job {JobId} created for title \"{Title}\" ({DiscType}) from raw directory {RawDir}",
            job.Id, title, parsedDiscType, rawDir);

        return job;
    }

    public async Task<int> RunImportTranscodeForJobAsync(int jobId, CancellationToken ct = default)
    {
        var job = await db.Jobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null)
        {
            logger.LogError("Import job {JobId} not found in DB", jobId);
            return 1;
        }

        var title = job.Title ?? "Unknown";
        var year = job.Year;
        var discType = job.DiscType.ToString();

        // ── Set up file logger and run ──
        using var _ = logger.BeginScope(new Dictionary<string, object>
        {
            [JobFileLoggerProvider.LogFilePathKey] = job.GetLogFilePath()
        });

        try
        {
            logger.LogInformation("************* Starting imported transcode *************");
            logger.LogInformation("Title: {Title} ({Year}) — {DiscType}", title, year, discType);
            logger.LogInformation("Raw directory: {DevPath}", job.DevPath);

            var directory = await armRipperService.RipVisualMediaAsync(job, job.LogFile ?? "", false, false, ct);
            job.Path = directory;

            if (job.Status is not JobState.Failure)
                job.Status = JobState.Success;
            job.StopTime = DateTime.UtcNow;
            if (job.StartTime != default)
            {
                var jobLength = job.StopTime.Value - job.StartTime;
                job.JobLength = $"{(int)jobLength.TotalHours}:{jobLength.Minutes:D2}:{jobLength.Seconds:D2}";
            }

            await db.SaveChangesAsync(ct);
            await BroadcastJobUpdateAsync(job);
            logger.LogInformation("************* Imported transcode complete *************");
            return 0;
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Imported transcode failed for job {JobId}", job.Id);
            job.Status = JobState.Failure;
            job.Errors = ex.Message;
            job.ProgressMessage = null;
            try { await db.SaveChangesAsync(ct); } catch { /* best effort */ }
            await BroadcastJobUpdateAsync(job);
            return 1;
        }
    }

    public async Task<int> RunImportTranscodeAsync(string rawFilePath, string title, string? year, string? videoType, string? discType, CancellationToken ct = default)
    {
        var job = await CreateImportJobAsync(rawFilePath, title, year, videoType, discType, ct);
        return await RunImportTranscodeForJobAsync(job.Id, ct);
    }

    private void Setup()
    {
        var armSettings = settings.Value;

        var directories = new[]
        {
            armSettings.RawPath,
            armSettings.TranscodePath,
            armSettings.CompletedPath,
            armSettings.LogPath,
            Path.Combine(armSettings.LogPath ?? "", "progress")
        };

        foreach (var dir in directories)
        {
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
        }
    }

    private async Task<Job> SetupJobAsync(string devicePath, CancellationToken ct)
    {
        // Create new job
        var job = new Job
        {
            DevPath = devicePath,
            Status = JobState.Active,
            StartTime = DateTime.UtcNow,
            DiscType = DiscType.Unknown
        };

        // Add job to DB
        db.Jobs.Add(job);
        await db.SaveChangesAsync(ct);

        job.LogFile = $"{job.Id}.log";
        job.TransitionToStage(RipStage.Setup);

        // Create config snapshot from effective settings (file + DB override)
        var armSettings = await SettingsHelper.GetEffectiveSettingsAsync(db, settings.Value, ct);
        var config = new ConfigSnapshot
        {
            JobId = job.Id,
            SkipTranscode = armSettings.SkipTranscode,
            MainFeature = armSettings.MainFeature,
            UseFfmpeg = armSettings.UseFfmpeg,
            ManualWait = armSettings.ManualWait,
            ManualWaitTime = armSettings.ManualWaitTime,
            AllowDuplicates = armSettings.AllowDuplicates,
            Prevent99 = armSettings.Prevent99,
            GetVideoTitle = armSettings.GetVideoTitle,
            GetAudioTitle = armSettings.GetAudioTitle,
            AutoEject = armSettings.AutoEject,
            DelRawFiles = armSettings.DelRawFiles,
            RawPath = armSettings.RawPath,
            TranscodePath = armSettings.TranscodePath,
            CompletedPath = armSettings.CompletedPath,
            LogPath = armSettings.LogPath,
            RipMethod = armSettings.RipMethod,
            MinLength = armSettings.MinLength,
            MaxLength = armSettings.MaxLength,
            HbPresetDvd = armSettings.HbPresetDvd,
            HbPresetBd = armSettings.HbPresetBd,
            HbArgsDvd = armSettings.HbArgsDvd,
            HbArgsBd = armSettings.HbArgsBd,
            DestExt = armSettings.DestExt,
            FfmpegCli = armSettings.FfmpegCli,
            FfmpegPreFileArgs = armSettings.FfmpegPreFileArgs,
            FfmpegPostFileArgs = armSettings.FfmpegPostFileArgs,
            MkvArgs = armSettings.MkvArgs,
            ExtrasSub = armSettings.ExtrasSub,
            InstallPath = armSettings.InstallPath,
            DbFile = armSettings.DbFile,
            NotifyRip = armSettings.NotifyRip,
            NotifyTranscode = armSettings.NotifyTranscode,
            PbKey = armSettings.PbKey,
            IftttKey = armSettings.IftttKey,
            PoUserKey = armSettings.PoUserKey,
            BashScript = armSettings.BashScript,
            JsonUrl = armSettings.JsonUrl,
            Apprise = armSettings.Apprise,
            OmdbApiKey = armSettings.OmdbApiKey,
            TmdbApiKey = armSettings.TmdbApiKey,
            ArmApiKey = armSettings.ArmApiKey,
            MetadataProvider = armSettings.MetadataProvider,
            WebServerPort = armSettings.WebServerPort,
            WebServerIp = armSettings.WebServerIp,
            UiBaseUrl = armSettings.UiBaseUrl,
            EmbyRefresh = armSettings.EmbyRefresh,
            EmbyServer = armSettings.EmbyServer,
            EmbyPort = armSettings.EmbyPort,
            EmbyApiKey = armSettings.EmbyApiKey,
            MaxConcurrentTranscodes = armSettings.MaxConcurrentTranscodes,
            MaxConcurrentMakemkvInfo = armSettings.MaxConcurrentMakemkvInfo,
            DiscDbEnabled = armSettings.DiscDbEnabled,
            DiscDbApiBaseUrl = armSettings.DiscDbApiBaseUrl,
            DiscDbMinConfidence = armSettings.DiscDbMinConfidence,
            DiscDbRequireConfirmation = armSettings.DiscDbRequireConfirmation
        };

        db.ConfigSnapshots.Add(config);
        job.MarkStageComplete(RipStage.Setup);
        await db.SaveChangesAsync(ct);

        // Log ARM parameters
        LogArmParams(job);

        logger.LogInformation("Job: {Label} created successfully", job.Label ?? devicePath);

        return job;
    }

    private async Task<int> ProcessJobAsync(Job job, CancellationToken ct)
    {
        // Route all ILogger output to the job's log file for this async scope
        using var _ = logger.BeginScope(new Dictionary<string, object>
        {
            [JobFileLoggerProvider.LogFilePathKey] = job.GetLogFilePath()
        });

        try
        {
            logger.LogInformation("Starting Disc identification");

            if (job.Status != JobState.Active)
            {
                var msg = $"Setup stage: expected status Active, was {job.Status}";
                logger.LogWarning(msg);
                job.Warnings = string.IsNullOrEmpty(job.Warnings) ? msg : $"{job.Warnings}; {msg}";
            }

            // Identify the disc
        job.TransitionToStage(RipStage.Identify);
        await db.SaveChangesAsync(ct);
        await identifyService.IdentifyAsync(job, ct);

        // ── Identification determined this job should not proceed? ──
        // DetectTrack99Async may have set job.Status = Failure (e.g. track 99
        // detected with PREVENT_99 enabled).  Check *now* before the manual
        // wait block (which defaults to true) overwrites the status.
        if (job.Status == JobState.Failure)
        {
            job.MarkStageComplete(RipStage.Identify);
            await db.SaveChangesAsync(ct);
            await BroadcastJobUpdateAsync(job);
            logger.LogError("Job {JobId} failed during identification: {Errors}", job.Id, job.Errors);
            return 1;
        }

        job.MarkStageComplete(RipStage.Identify);
        await db.SaveChangesAsync(ct);
        await BroadcastJobUpdateAsync(job);

        if (await IsCancelledAsync(job, ct))
            return 1;

        // Check for duplicates
        var haveDupes = await JobDupeCheckAsync(job, ct);
        logger.LogDebug("Value of have_dupes: {HaveDupes}", haveDupes);

        // ── Duplicate disc: skip the rip entirely ──
        // If this disc (identified by Label) has already been successfully ripped
        // and AllowDuplicates is false, cleanly skip re-ripping to prevent the
        // auto-detect loop: disc finishes → ejects → tray closes → disc detected
        // → would start ripping again.
        var cfg = job.Config ?? db.ConfigSnapshots.FirstOrDefault(c => c.JobId == job.Id);
        var allowDupes = cfg?.AllowDuplicates ?? settings.Value.AllowDuplicates;
        if (haveDupes && !allowDupes)
        {
            logger.LogInformation(
                "Disc '{Label}' (job {JobId}) has already been ripped successfully. " +
                "AllowDuplicates is disabled — marking job as completed without re-ripping.",
                job.Label, job.Id);
            job.Status = JobState.Success;
            job.StopTime = DateTime.UtcNow;
            job.ProgressMessage = $"Duplicate disc skipped — previously ripped as \"{job.Title}\"";
            job.Path = job.Label;
            await db.SaveChangesAsync(ct);
            await BroadcastJobUpdateAsync(job);
            fileLogProvider.RemoveWriter(job.GetLogFilePath());
            return 0;
        }

        // Manual wait for title identification
        if (cfg is { ManualWait: true } && string.IsNullOrEmpty(job.TitleManual) && !string.IsNullOrEmpty(job.Label))
        {
            var waitTime = cfg.ManualWaitTime > 0 ? cfg.ManualWaitTime : 60;
            logger.LogInformation("Waiting {Time}s for manual title override", waitTime);
            job.Status = JobState.ManualWaitStarted;
            job.ProgressMessage = $"Manual wait: {waitTime}s remaining";
            await db.SaveChangesAsync(ct);
            await BroadcastJobUpdateAsync(job);

            var waited = 0;
            while (waited < waitTime)
            {
                await Task.Delay(5000, ct);
                waited += 5;

                // Refresh job to check for UI changes
                await db.Entry(job).ReloadAsync(ct);

                if (job.Status == JobState.Cancelled)
                {
                    logger.LogInformation("Job cancelled during manual wait");
                    return 1;
                }

                if (!string.IsNullOrEmpty(job.TitleManual))
                {
                    logger.LogInformation("Manual title override found: {Title}", job.TitleManual);
                    break;
                }

                if (job.ManualWaitResume)
                {
                    logger.LogInformation("Manual wait resumed by user");
                    job.ManualWaitResume = false;
                    await db.SaveChangesAsync(ct);
                    await BroadcastJobUpdateAsync(job);
                    break;
                }

                // Update countdown
                var remaining = waitTime - waited;
                if (remaining > 0)
                {
                    job.ProgressMessage = $"Manual wait: {remaining}s remaining";
                    await db.SaveChangesAsync(ct);
                    await BroadcastJobUpdateAsync(job);
                }
            }

            if (string.IsNullOrEmpty(job.TitleManual))
                logger.LogInformation("Manual wait expired, continuing with auto-identified title");

            job.Status = JobState.Active;
            job.ProgressMessage = "Starting rip...";
            await db.SaveChangesAsync(ct);
            await BroadcastJobUpdateAsync(job);
        }

        // Notify entry
        await notificationService.NotifyEntryAsync(job, ct);

        // Dispatch based on disc type
        if (job.Status == JobState.Failure)
        {
            logger.LogError("Job {JobId} failed during identification: {Errors}", job.Id, job.Errors);
            return 1;
        }

        switch (job.DiscType)
        {
            case DiscType.Dvd:
            case DiscType.Bluray:
            case DiscType.Uhd:
                if (await IsCancelledAsync(job, ct))
                    return 1;
                logger.LogInformation("Disc identified as video. Starting rip.");
                var directory = await armRipperService.RipVisualMediaAsync(job, job.LogFile ?? "", haveDupes, job.HasTrack99, ct);
                job.Path = directory;
                break;

            case DiscType.Music:
                logger.LogInformation("Disc identified as music");
                var musicTitle = await musicBrainzService.IdentifyAsync(job, ct);
                if (!string.IsNullOrEmpty(musicTitle))
                    logger.LogInformation("Music CD identified: {Title}", musicTitle);

                await RipMusicAsync(job, ct);
                await identifyService.EjectAsync(job, ct);
                job.Ejected = true;
                break;

            case DiscType.Data:
                logger.LogInformation("Disc identified as data");
                await RipDataAsync(job, ct);
                await identifyService.EjectAsync(job, ct);
                job.Ejected = true;
                break;

            default:
                logger.LogCritical("Couldn't identify the disc type. Exiting without any action.");
                job.Status = JobState.Failure;
                await db.SaveChangesAsync(ct);
                await BroadcastJobUpdateAsync(job);
                return 1;
        }

        // Verify output files exist before marking Success
        if (job.Status is not JobState.Failure && job.Path is not null && Directory.Exists(job.Path))
        {
            if (!Directory.EnumerateFileSystemEntries(job.Path).Any())
            {
                var msg = $"Job completed but no output files found in {job.Path}";
                logger.LogError(msg);
                job.Status = JobState.Failure;
                job.Errors = msg;
            }
        }

        if (job.Status is not JobState.Failure)
            job.Status = JobState.Success;
        job.StopTime = DateTime.UtcNow;
        if (job.StartTime != default)
        {
            var jobLength = job.StopTime.Value - job.StartTime;
            job.JobLength = $"{(int)jobLength.TotalHours}:{jobLength.Minutes:D2}:{jobLength.Seconds:D2}";
        }

        await db.SaveChangesAsync(ct);
        await BroadcastJobUpdateAsync(job);
        logger.LogInformation("************* ARM processing complete *************");
        return 0;
        }
        finally
        {
            fileLogProvider.RemoveWriter(job.GetLogFilePath());
        }
    }

    private async Task RipMusicAsync(Job job, CancellationToken ct)
    {
        var abcFile = job.Config?.InstallPath is not null
            ? Path.Combine(job.Config.InstallPath, "abcde.conf")
            : "/etc/arm/config/abcde.conf";

        var cmd = File.Exists(abcFile)
            ? $"abcde -d \"{job.DevPath}\" -c \"{abcFile}\" >> \"{Path.Combine(job.Config?.LogPath ?? "", job.LogFile ?? "")}\" 2>&1"
            : $"abcde -d \"{job.DevPath}\" >> \"{Path.Combine(job.Config?.LogPath ?? "", job.LogFile ?? "")}\" 2>&1";

        logger.LogDebug("Sending command: {Command}", cmd);
        job.TransitionToStage(RipStage.Rip);
        job.Status = JobState.AudioRipping;
        await db.SaveChangesAsync(ct);
        await BroadcastJobUpdateAsync(job);

        try
        {
            await runner.RunAsync("bash", $"-c \"{cmd.Replace("\"", "\\\"")}\"", timeoutMs: 7200_000, ct: ct);
            logger.LogInformation("abcde call successful");
            job.TransitionToStage(RipStage.Done);
            job.Status = JobState.Active;
        }
        catch (Exception ex)
        {
            var err = $"Call to abcde failed: {ex.Message}";
            logger.LogError(err);
            job.Status = JobState.Failure;
            job.Errors = err;
        }

        await db.SaveChangesAsync(ct);
        await BroadcastJobUpdateAsync(job);
    }

    private async Task RipDataAsync(Job job, CancellationToken ct)
    {
        var label = !string.IsNullOrEmpty(job.Label) ? job.Label : "data-disc";
        var rawPath = job.Config?.RawPath is not null
            ? Path.Combine(job.Config.RawPath, label)
            : Path.Combine(ArmPaths.GetRawPath(settings.Value), label);
        var finalDir = job.Config?.CompletedPath is not null
            ? Path.Combine(job.Config.CompletedPath, ArmPaths.DataDir)
            : Path.Combine(ArmPaths.GetCompletedPath(settings.Value), ArmPaths.DataDir);
        var finalFileName = label;

        if (Directory.Exists(rawPath))
        {
            var timeSuffix = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
            rawPath = $"{rawPath}_{timeSuffix}";
            finalFileName = $"{label}_{timeSuffix}";
        }

        if (!Directory.Exists(rawPath))
            Directory.CreateDirectory(rawPath);

        var finalPath = Path.Combine(finalDir, finalFileName);
        var incompleteFilename = Path.Combine(rawPath, $"{label}.part");

        if (!Directory.Exists(finalPath))
            Directory.CreateDirectory(finalPath);

        logger.LogInformation("Ripping data disc to: {IncompleteFilename}", incompleteFilename);

        var cmd = $"dd if=\"{job.DevPath}\" of=\"{incompleteFilename}\" bs=2048 conv=noerror,sync status=progress 2>> \"{Path.Combine(job.Config?.LogPath ?? "", job.LogFile ?? "")}\"";

        job.TransitionToStage(RipStage.Rip);
        await db.SaveChangesAsync(ct);
        await BroadcastJobUpdateAsync(job);

        try
        {
            await runner.RunAsync("bash", $"-c \"{cmd.Replace("\"", "\\\"")}\"", timeoutMs: 7200_000, ct: ct);
            var fullFinalFile = Path.Combine(finalPath, $"{label}.iso");
            logger.LogInformation("Moving data-disc from '{Src}' to '{Dst}'", incompleteFilename, fullFinalFile);
            if (File.Exists(incompleteFilename))
                File.Move(incompleteFilename, fullFinalFile);
            logger.LogInformation("Data rip call successful");
            job.TransitionToStage(RipStage.Done);
        }
        catch (Exception ex)
        {
            var err = $"Data rip failed: {ex.Message}";
            logger.LogError(err);
            job.Status = JobState.Failure;
            job.Errors = err;
            try { File.Delete(incompleteFilename); } catch { }
        }

        await db.SaveChangesAsync(ct);
        await BroadcastJobUpdateAsync(job);

        try
        {
            if (Directory.Exists(rawPath))
                Directory.Delete(rawPath, recursive: true);
        }
        catch { }
    }

    private async Task<bool> IsCancelledAsync(Job job, CancellationToken ct)
    {
        await db.Entry(job).ReloadAsync(ct);
        if (job.Status is JobState.Cancelled or JobState.Stopping)
        {
            logger.LogInformation("Job was cancelled/stopping, aborting");
            return true;
        }
        return false;
    }

    private async Task<bool> JobDupeCheckAsync(Job job, CancellationToken ct)
    {
        // ── Determine which identity fields we can match on ──
        // Strong identifiers (CrcId, DiscDbHash) prove the exact same disc content.
        // Weak identifier (Label) matches the volume name but may cover multiple
        // pressings/regions of the same movie, so we only use it as a fallback
        // when no strong identifier is available.
        var hasCrc = !string.IsNullOrEmpty(job.CrcId);
        var hasDiscDb = !string.IsNullOrEmpty(job.DiscDbHash);
        var hasLabel = !string.IsNullOrEmpty(job.Label);

        if (!hasCrc && !hasDiscDb && !hasLabel)
        {
            logger.LogInformation("Disc has no Label, CrcId, or DiscDbHash — cannot check for duplicates");
            return false;
        }

        // ── Phase 1: Check for fully-completed (Success) duplicates ──
        // Build the match query: prefer strong identifiers, fall back to Label
        IQueryable<Job> query = db.Jobs.Where(j => j.Status == JobState.Success);

        if (hasCrc || hasDiscDb)
        {
            // Exact disc match via content hashes
            if (hasCrc)
                query = query.Where(j => j.CrcId == job.CrcId);
            else
                query = query.Where(j => j.DiscDbHash == job.DiscDbHash);

            logger.LogInformation(
                "Checking duplicates by {Field} = '{Value}'",
                hasCrc ? "CrcId" : "DiscDbHash",
                hasCrc ? job.CrcId : job.DiscDbHash);
        }
        else
        {
            // Fall back to volume-label match when no hash identity is available
            query = query.Where(j => j.Label == job.Label);

            logger.LogInformation(
                "Checking duplicates by Label = '{Label}' (no CrcId or DiscDbHash available)",
                job.Label);
        }

        var previousRips = await query
            .OrderByDescending(j => j.StopTime)
            .Select(j => new { j.Title, j.Year, j.HasNiceTitle, j.VideoType, j.PosterUrl })
            .Take(2)
            .ToListAsync(ct);

        if (previousRips.Count == 1)
        {
            var prev = previousRips[0];
            job.Title = job.TitleAuto = prev.Title ?? job.Label;
            job.Year = job.YearAuto = prev.Year;
            job.HasNiceTitle = prev.HasNiceTitle;
            job.VideoType = job.VideoTypeAuto = prev.VideoType;
            job.PosterUrl = job.PosterUrlAuto = prev.PosterUrl;
            await db.SaveChangesAsync(ct);
            return true;
        }

        if (previousRips.Count > 1)
        {
            logger.LogDebug("Skipping - There are too many results [{Count}]", previousRips.Count);
            return false;
        }

        // ── Phase 2: Check for in-flight duplicates (strong identity only) ──
        // When a disc finishes ripping, the drive is ejected mid-pipeline (before transcode).
        // If the tray auto-closes and the same disc is reinserted, a new job starts while the
        // previous job is still processing (e.g. transcoding).  In that window the previous job
        // hasn't reached Success yet, so Phase 1 above won't catch it.
        //
        // We detect this by looking for any non-terminal job that:
        //   - Shares the same strong identifier (CrcId or DiscDbHash)
        //   - Has already completed the Rip stage (disc content was read)
        //   - Is not this same job
        //
        // The Label fallback intentionally excluded here — weak identity isn't reliable enough
        // to make assumptions about in-flight jobs.
        if (hasCrc || hasDiscDb)
        {
            var inFlightDupe = await db.Jobs
                .Where(j => j.Id != job.Id)
                .Where(j => j.Status != JobState.Failure && j.Status != JobState.Cancelled)
                .Where(j => !string.IsNullOrEmpty(j.CompletedStages) && j.CompletedStages.Contains("Rip"))
                .Where(j => hasCrc ? j.CrcId == job.CrcId : j.DiscDbHash == job.DiscDbHash)
                .Select(j => new { j.Title, j.Year, j.HasNiceTitle, j.VideoType, j.PosterUrl })
                .FirstOrDefaultAsync(ct);

            if (inFlightDupe is not null)
            {
                logger.LogInformation(
                    "Disc '{Label}' (job {JobId}) is already being processed by job (same {Field}). " +
                    "That job has completed the Rip stage — marking this job as duplicate without re-ripping.",
                    job.Label, job.Id,
                    hasCrc ? "CrcId" : "DiscDbHash");

                // Copy metadata from the in-flight job so the skip path uses correct naming
                job.Title = job.TitleAuto = inFlightDupe.Title ?? job.Label;
                job.Year = job.YearAuto = inFlightDupe.Year;
                job.HasNiceTitle = inFlightDupe.HasNiceTitle;
                job.VideoType = job.VideoTypeAuto = inFlightDupe.VideoType;
                job.PosterUrl = job.PosterUrlAuto = inFlightDupe.PosterUrl;
                await db.SaveChangesAsync(ct);
                return true;
            }
        }

        logger.LogInformation("We have no previous rips/jobs matching this label");
        return false;
    }

    private void LogArmParams(Job job)
    {
        logger.LogInformation("******************* Logging ARM variables *******************");
        foreach (var key in new[] { "devpath", "mountpoint", "title", "year", "video_type",
            "hasnicetitle", "label", "disctype", "manual_start" })
        {
            var value = key switch
            {
                "devpath" => job.DevPath,
                "mountpoint" => job.MountPoint,
                "title" => job.Title,
                "year" => job.Year,
                "video_type" => job.VideoType,
                "hasnicetitle" => job.HasNiceTitle.ToString(),
                "label" => job.Label,
                "disctype" => job.DiscType.ToString(),
                "manual_start" => job.ManualStart.ToString(),
                _ => ""
            };
            logger.LogInformation("{Key}: {Value}", key, value);
        }
        logger.LogInformation("******************* End of ARM variables *******************");
    }
}
