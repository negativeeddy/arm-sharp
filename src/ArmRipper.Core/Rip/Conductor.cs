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
            Setup();
            job = await SetupJobAsync(devicePath, ct);
            return await ProcessJobAsync(job, ct);
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "A fatal error has occurred and ARM is exiting");
            if (job is not null)
            {
                job.Status = JobState.Failure;
                job.Errors = ex.Message;
                job.ProgressMessage = null;
                try { await db.SaveChangesAsync(ct); } catch { /* best effort */ }
                await BroadcastJobUpdateAsync(job);
            }
            return 1;
        }
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
        job.Stage = RipStage.Setup;

        // Create config snapshot
        var armSettings = settings.Value;
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
            MaxConcurrentRips = armSettings.MaxConcurrentRips,
            MaxConcurrentTranscodes = armSettings.MaxConcurrentTranscodes,
            MaxConcurrentMakemkvInfo = armSettings.MaxConcurrentMakemkvInfo
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
        job.Stage = RipStage.Identify;
        await db.SaveChangesAsync(ct);
        await identifyService.IdentifyAsync(job, ct);

        job.MarkStageComplete(RipStage.Identify);
        await db.SaveChangesAsync(ct);
        await BroadcastJobUpdateAsync(job);

        if (await IsCancelledAsync(job, ct))
            return 1;

        // Check for duplicates
        var haveDupes = await JobDupeCheckAsync(job, ct);
        logger.LogDebug("Value of have_dupes: {HaveDupes}", haveDupes);

        // Manual wait for title identification
        var cfg = job.Config ?? db.ConfigSnapshots.FirstOrDefault(c => c.JobId == job.Id);
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
        switch (job.DiscType)
        {
            case DiscType.Dvd:
            case DiscType.Bluray:
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
                break;

            case DiscType.Data:
                logger.LogInformation("Disc identified as data");
                await RipDataAsync(job, ct);
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
        job.Stage = RipStage.Rip;
        job.Status = JobState.AudioRipping;
        await db.SaveChangesAsync(ct);
        await BroadcastJobUpdateAsync(job);

        try
        {
            await runner.RunAsync("bash", $"-c \"{cmd.Replace("\"", "\\\"")}\"", timeoutMs: 7200_000, ct: ct);
            logger.LogInformation("abcde call successful");
            job.Stage = RipStage.Done;
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
            : Path.Combine(settings.Value.RawPath ?? "/home/arm/media/raw", label);
        var finalDir = job.Config?.CompletedPath is not null
            ? Path.Combine(job.Config.CompletedPath, "data")
            : Path.Combine(settings.Value.CompletedPath ?? "/home/arm/media", "data");
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

        job.Stage = RipStage.Rip;
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
            job.Stage = RipStage.Done;
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
        if (job.Status == JobState.Cancelled)
        {
            logger.LogInformation("Job was cancelled, aborting");
            return true;
        }
        return false;
    }

    private async Task<bool> JobDupeCheckAsync(Job job, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(job.Label))
        {
            logger.LogInformation("Disc title 'None' not searched in database");
            return false;
        }

        var previousRips = await db.Jobs
            .Where(j => j.Label == job.Label && j.Status == JobState.Success)
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
