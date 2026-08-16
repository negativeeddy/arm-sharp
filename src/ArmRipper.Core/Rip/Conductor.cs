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
    ISettingsService settingsService,
    IIdentifyService identifyService,
    IArmRipperService armRipperService,
    IMusicBrainzService musicBrainzService,
    NotificationService notificationService,
    IEnumerable<INotificationBroadcaster> broadcasters,
    JobFileLoggerProvider fileLogProvider) : IConductor
{
    private readonly ILogger logger = loggerFactory.CreateLogger("Conductor");
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
            // Resolve effective settings (DB overrides win) for the directory setup so
            // paths imported via ARM settings / saved in the DB are honored.
            var effectiveSetupSettings = await settingsService.GetEffectiveAsync(ct);
            Setup(effectiveSetupSettings);
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
                catch (Exception ex) { logger.LogDebug(ex, "Failed to persist Stopping status during shutdown for job {JobId}", job?.Id); }
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
                try { await db.SaveChangesAsync(CancellationToken.None); }
                catch (Exception ex2) { logger.LogDebug(ex2, "Failed to persist failure status for job {JobId}", job.Id); }
                await BroadcastJobUpdateAsync(job);
            }
            return 1;
        }
    }

    /// <summary>
    /// Resumes a previously stopped/cancelled job from its last completed stage.
    /// Loads the existing job from the database and proceeds through the pipeline,
    /// skipping stages already marked in <see cref="Job.CompletedStages"/>.
    /// </summary>
    public async Task<int> RunResumeAsync(int jobId, CancellationToken ct = default)
    {
        var job = await db.Jobs
            .Include(j => j.Config)
            .FirstOrDefaultAsync(j => j.Id == jobId, ct);

        if (job is null)
        {
            logger.LogError("Cannot resume job {JobId} — not found in database", jobId);
            return 1;
        }

        try
        {
            var effectiveSetupSettings = await settingsService.GetEffectiveAsync(ct);
            Setup(effectiveSetupSettings);
            logger.LogInformation("Resuming job {JobId} from completed stages: {Stages}",
                job.Id, job.CompletedStages ?? "(none)");
            return await ProcessJobAsync(job, ct);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Resumed job {JobId} cancelled (token)", job.Id);
            if (!job.Status.IsTerminal() && job.Status != JobState.Stopping)
            {
                job.Status = JobState.Stopping;
                job.StopTime ??= DateTime.UtcNow;
                job.ProgressMessage = "Cancelled — can be resumed";
                try { await db.SaveChangesAsync(CancellationToken.None); }
                catch (Exception ex) { logger.LogDebug(ex, "Failed to persist Stopping status for resumed job {JobId}", job.Id); }
                await BroadcastJobUpdateAsync(job);
            }
            return 1;
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Fatal error resuming job {JobId}", job.Id);
            if (job.Status != JobState.Stopping)
            {
                job.Status = JobState.Failure;
                job.Errors = ex.Message;
                job.ProgressMessage = null;
                try { await db.SaveChangesAsync(CancellationToken.None); }
                catch (Exception ex2) { logger.LogDebug(ex2, "Failed to persist failure status for resumed job {JobId}", job.Id); }
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
    /// <param name="discType">Optional override for the disc type (e.g. "dvd", "bluray").</param>
    /// <param name="videoType">Optional override for the video type (e.g. "movie", "series").</param>
    public async Task<int> RunForkedTranscodeAsync(int originalJobId, string rawFilePath, CancellationToken ct = default, DiscType? discType = null, VideoContentType? videoType = null, ArmSettings? effectiveSettings = null)
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

        // ── 1b. Resolve disc type override ──
        var resolvedDiscType = discType ?? originalJob.DiscType;

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
            VideoType = videoType ?? originalJob.VideoType,
            VideoTypeAuto = originalJob.VideoTypeAuto,
            DiscType = resolvedDiscType,
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

        // ── 3. Build config snapshot from current effective settings.
        //     For a forked transcode the user expects current settings (GPU,
        //     DelRawFiles, presets, etc.) to apply — NOT the stale snapshot
        //     from the original job.  We only carry forward disc-specific
        //     behavioural overrides (MainFeature, RipMethod, etc.) that were
        //     chosen for this particular disc. ──
        var armSettings = effectiveSettings ?? settings.Value;
        var sourceConfig = originalJob.Config;

        // Disc-specific overrides from the original job are carried forward
        // via the carryForward parameter; current settings take precedence
        // for everything else.
        var config = ConfigSnapshot.FromSettings(armSettings, job.Id, sourceConfig);
        config.AutoEject = false; // Don't eject — no physical disc
        config.NotifyRip = false; // Skip rip notifications

        db.ConfigSnapshots.Add(config);
        job.MarkStageComplete(RipStage.Setup);
        job.MarkStageComplete(RipStage.Identify);
        job.MarkStageComplete(RipStage.Rip);
        await db.SaveChangesAsync(ct);

        // Attach the config snapshot to the in-memory job so that HandBrakeService
        // (which reads job.Config?.GpuIndex, job.Config?.HbPresetBd, etc.) picks up
        // the correct per-job overrides instead of falling through to IOptions defaults.
        job.Config = config;

        logger.LogInformation("Forked job {JobId} created from original job {OriginalJobId} for raw directory {RawDir}",
            job.Id, originalJob.Id, rawDir);

        // ── 4. Notify that a forked transcode has started ──
        if (config.NotifyTranscode)
        {
            await notificationService.NotifyAsync(job, NotificationService.NotifyTitle,
                $"Forked transcode started for {job.Title} — job #{job.Id} (forked from #{originalJob.Id}, {job.DiscType}, {job.VideoType})", ct);
        }

        // ── 5. Set up file logger and run ──
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
            try { await db.SaveChangesAsync(ct); }
            catch (Exception ex2) { logger.LogDebug(ex2, "Failed to persist failure status for job {JobId}", job.Id); }
            await BroadcastJobUpdateAsync(job);
            return 1;
        }
    }

    /// <summary>
    /// Creates a new standalone job from raw MKV files that were ripped elsewhere,
    /// skipping identify and rip stages — jumps straight to transcoding.
    /// </summary>
    public async Task<Job> CreateImportJobAsync(string rawFilePath, string title, string? year, VideoContentType? videoType, DiscType? discType, ArmSettings? effectiveSettings = null, CancellationToken ct = default)
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
        var parsedDiscType = discType ?? DiscType.Bluray; // safest default for imported MKVs

        // ── 3. Create the job with user-provided metadata ──
        //     Use effectiveSettings (merged YAML + DB overrides) when available,
        //     otherwise fall back to IOptions defaults (which only have YAML values).
        var armSettings = effectiveSettings ?? settings.Value;
        var job = new Job
        {
            DevPath = rawDir,
            Status = JobState.Active,
            StartTime = DateTime.UtcNow,
            Title = title,
            TitleAuto = title,
            Year = year ?? "0000",
            YearAuto = year ?? "0000",
            VideoType = videoType ?? VideoContentType.Movie,
            VideoTypeAuto = videoType ?? VideoContentType.Movie,
            DiscType = parsedDiscType,
            Label = title,
            ManualStart = true
        };

        db.Jobs.Add(job);
        await db.SaveChangesAsync(ct);

        job.LogFile = $"{job.Id}.log";
        job.TransitionToStage(RipStage.Setup);

        // ── 4. Create config snapshot from current settings ──
        var config = ConfigSnapshot.FromSettings(armSettings, job.Id);
        config.ManualWait = false;
        config.GetVideoTitle = false;
        config.AutoEject = false;
        config.DelRawFiles = false; // Never auto-delete imported raw files
        config.NotifyRip = false;

        db.ConfigSnapshots.Add(config);
        job.MarkStageComplete(RipStage.Setup);
        job.MarkStageComplete(RipStage.Identify);
        job.MarkStageComplete(RipStage.Rip);
        await db.SaveChangesAsync(ct);

        // Attach the config snapshot so transcode services see the correct
        // GPU, preset, and argument overrides.
        job.Config = config;

        logger.LogInformation("Import job {JobId} created for title \"{Title}\" ({DiscType}) from raw directory {RawDir}",
            job.Id, title, parsedDiscType, rawDir);

        return job;
    }

    public async Task<int> RunImportTranscodeForJobAsync(int jobId, CancellationToken ct = default)
    {
        var job = await db.Jobs
            .Include(j => j.Config)
            .FirstOrDefaultAsync(j => j.Id == jobId, ct);
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

            // Notify that an import transcode has started
            if (job.Config?.NotifyTranscode ?? settings.Value.NotifyTranscode)
            {
                await notificationService.NotifyAsync(job, NotificationService.NotifyTitle,
                    $"Import transcode started for {job.Title} — job #{job.Id} ({job.DiscType}, {job.VideoType})", ct);
            }

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
            try { await db.SaveChangesAsync(ct); }
            catch (Exception ex2) { logger.LogDebug(ex2, "Failed to persist failure status for job {JobId}", job.Id); }
            await BroadcastJobUpdateAsync(job);
            return 1;
        }
    }

    public async Task<int> RunImportTranscodeAsync(string rawFilePath, string title, string? year, VideoContentType? videoType, DiscType? discType, CancellationToken ct = default)
    {
        var job = await CreateImportJobAsync(rawFilePath, title, year, videoType, discType, effectiveSettings: null, ct);
        return await RunImportTranscodeForJobAsync(job.Id, ct);
    }

    private void Setup(ArmSettings armSettings)
    {
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
        var armSettings = await settingsService.GetEffectiveAsync(ct);
        var config = ConfigSnapshot.FromSettings(armSettings, job.Id);

        // Per-drive Main Feature override (issue #62): if the drive has a saved
        // override it wins over the global setting for this job, so the same
        // machine can rip a movie (main feature) in one drive and a series
        // (all titles) in another.
        var drive = await db.SystemDrives.FirstOrDefaultAsync(d => d.Mount == devicePath, ct);
        if (drive?.MainFeature is bool mainFeatureOverride)
        {
            config.MainFeature = mainFeatureOverride;
            logger.LogInformation(
                "Applying per-drive Main Feature override ({Mode}) for {DevPath}",
                mainFeatureOverride ? "main" : "all", devicePath);
        }

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
                if (job.Status.IsResumable())
                {
                    logger.LogInformation("Job {JobId} is resumable ({Status}) — proceeding", job.Id, job.Status);
                }
                else
                {
                    logger.LogWarning("Job {JobId} has non-Active status {Status} — aborting", job.Id, job.Status);
                    return 1;
                }
            }

            var cfg = job.Config ?? await db.ConfigSnapshots
                .FirstOrDefaultAsync(c => c.JobId == job.Id, ct);
            var isResume = job.IsStageComplete(RipStage.Identify);

            if (isResume)
            {
                logger.LogInformation("Resume: skipping Identify stage (already complete)");
                job.TransitionToStage(RipStage.Rip);
            }
            else
            {
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
            }

            // ── Duplicate check & manual wait — only on first run ──
            bool haveDupes = false;
            if (!isResume)
            {
                haveDupes = await JobDupeCheckAsync(job, ct);
                logger.LogDebug("Value of have_dupes: {HaveDupes}", haveDupes);

                // ── Duplicate disc: skip the rip entirely ──
                // If this disc (identified by Label) has already been successfully ripped
                // and AllowDuplicates is false, cleanly skip re-ripping to prevent the
                // auto-detect loop: disc finishes → ejects → tray closes → disc detected
                // → would start ripping again.
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

                    // The wait loop only checks for Cancelled explicitly — another process
                    // may have set a terminal state (e.g. Failure) meanwhile. Reload from the
                    // DB and abort rather than overwriting that state.
                    await db.Entry(job).ReloadAsync(ct);
                    if (job.Status.IsTerminal())
                    {
                        logger.LogWarning("Job set to terminal state {Status} during manual wait — aborting", job.Status);
                        return 1;
                    }

                    job.Status = JobState.Active;
                    job.ProgressMessage = "Starting rip...";
                    await db.SaveChangesAsync(ct);
                    await BroadcastJobUpdateAsync(job);
                }
            }
            else
            {
                logger.LogInformation("Resume: skipping duplicate check & manual wait");
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
                // Mark Identify complete for consistent CompletedStages tracking across
                // all failure paths (identification ran, it just couldn't determine the type).
                job.MarkStageComplete(RipStage.Identify);
                job.Status = JobState.Failure;
                job.Errors = "Couldn't identify the disc type. Exiting without any action.";
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
        var effective = await settingsService.GetEffectiveAsync(ct);
        var rawPath = job.Config?.RawPath is not null
            ? Path.Combine(job.Config.RawPath, label)
            : Path.Combine(ArmPaths.GetRawPath(effective), label);
        var finalDir = job.Config?.CompletedPath is not null
            ? Path.Combine(job.Config.CompletedPath, ArmPaths.DataDir)
            : Path.Combine(ArmPaths.GetCompletedPath(effective), ArmPaths.DataDir);
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
            try { File.Delete(incompleteFilename); }
            catch (Exception ex2) { logger.LogDebug(ex2, "Failed to delete incomplete data-disc file {Path}", incompleteFilename); }
        }

        await db.SaveChangesAsync(ct);
        await BroadcastJobUpdateAsync(job);

        try
        {
            if (Directory.Exists(rawPath))
                Directory.Delete(rawPath, recursive: true);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to remove raw data-disc directory {Path}", rawPath);
        }
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
            job.VideoTypeAuto = prev.VideoType;
            job.VideoType = prev.VideoType;
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
                .Where(JobStageQueryHelper.HasCompletedStage(RipStage.Rip))
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
                job.VideoTypeAuto = inFlightDupe.VideoType;
                job.VideoType = inFlightDupe.VideoType;
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
                "video_type" => job.VideoType.ToString().ToLowerInvariant(),
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
