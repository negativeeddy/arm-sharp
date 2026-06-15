using ArmRipper.Core.Configuration;
using ArmRipper.Core.Infrastructure;
using ArmRipper.Core.Infrastructure.Data;
using ArmRipper.Core.Models;
using ArmRipper.Core.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArmRipper.Core.Rip;

public sealed class ArmRipperService(
    ILoggerFactory loggerFactory,
    ArmDbContext db,
    IMakeMkvService makeMkv,
    IHandBrakeService handBrake,
    IFfmpegService ffmpeg,
    ICliProcessRunner runner,
    NotificationService notifications,
    IOptions<ArmSettings> settings,
    IEnumerable<INotificationBroadcaster> broadcasters) : IArmRipperService
{
    private readonly ILogger logger = loggerFactory.CreateLogger("ArmRipperService");
    public async Task<string> RipVisualMediaAsync(Job job, string logFile, bool hasDupes, bool protection, CancellationToken ct = default)
    {
        // ── 1. Compute paths ──
        var typeSubFolder = ConvertJobType(job.VideoType);
        var jobTitle = FixJobTitle(job);

        var transcodeOutPath = Path.Combine(job.Config?.TranscodePath ?? settings.Value.TranscodePath!, typeSubFolder, jobTitle);
        var finalDirectory = Path.Combine(job.Config?.CompletedPath ?? settings.Value.CompletedPath!, typeSubFolder, jobTitle);

        job.Stage ??= RipStage.Setup;
        job.Stage = RipStage.Identify;  // transition stage
        job.ProgressMessage ??= "Preparing to rip...";
        await db.SaveChangesAsync(ct);
        BroadcastJobUpdate(job);

        transcodeOutPath = CheckForDupeFolder(hasDupes, transcodeOutPath, job);
        finalDirectory = CheckForDupeFolder(hasDupes, finalDirectory, job);

        job.Path = finalDirectory;
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Processing files to: {TranscodeOutPath}", transcodeOutPath);

        var makeMkvOutPath = Path.Combine(job.Config?.RawPath ?? settings.Value.RawPath!, jobTitle);
        var transcodeInPath = job.DevPath;
        var useMakeMkv = RipWithMkv(job, protection);

        logger.LogDebug("Using MakeMKV: {UseMakeMkv}", useMakeMkv);

        // ── 2. MakeMKV rip (idempotent) ──
        if (useMakeMkv)
        {
            if (job.IsStageComplete(RipStage.Rip))
            {
                logger.LogInformation("Stage 'rip' already completed — skipping MakeMKV rip");
                transcodeInPath = makeMkvOutPath;
                goto afterMakeMkv;
            }

            if (settings.Value.TestMode)
            {
                logger.LogInformation("Test mode: ripping track 0 directly");

                if (!Directory.Exists(makeMkvOutPath))
                    Directory.CreateDirectory(makeMkvOutPath);

                var mkvArgs = job.Config?.MkvArgs ?? settings.Value.MkvArgs ?? "";
                var minLength = job.Config?.MinLength ?? settings.Value.MinLength;
                await makeMkv.RipTrackAsync(job, "0", makeMkvOutPath, mkvArgs, minLength, MkvProgress(job, "Ripping track 0", ct), ct);
                logger.LogInformation("Ripped track 0 in test mode");

                transcodeInPath = makeMkvOutPath;
                goto afterMakeMkv;
            }

            {
                logger.LogInformation("************* Getting track info from MakeMKV *************");

                var config = job.Config;
                var minLength = config?.MinLength ?? settings.Value.MinLength;
                var maxLength = config?.MaxLength ?? settings.Value.MaxLength;

                var tracks = await makeMkv.GetTrackInfoWithCacheAsync(job, jobTitle, ct);

                // Encrypted BDs often return 0 tracks from info; rip all titles directly
                if (tracks.Count == 0 && job.DiscType is DiscType.Bluray or DiscType.Dvd)
                {
                    job.Stage = RipStage.Identify;
                    GuardStage(job, "identify", "Active/VideoInfo", () => job.Status is JobState.Active or JobState.VideoInfo);
                    job.Stage = RipStage.Rip;
                    job.Status = JobState.VideoRipping;
                    job.ProgressMessage = "Starting rip...";
                    await db.SaveChangesAsync(ct);
                    BroadcastJobUpdate(job);

                    if (!Directory.Exists(makeMkvOutPath))
                        Directory.CreateDirectory(makeMkvOutPath);

                    var mkvArgs = config?.MkvArgs ?? settings.Value.MkvArgs ?? "";
                        await makeMkv.RipAllTitlesAsync(job, makeMkvOutPath, mkvArgs, minLength, MkvProgress(job, "Ripping all titles", ct), ct);
                    logger.LogInformation("Ripped all titles from disc (0-track fallback)");

                    if (!Directory.EnumerateFileSystemEntries(makeMkvOutPath).Any())
                    {
                        var msg = "MakeMKV rip produced no output files";
                        logger.LogError(msg);
                        throw new InvalidOperationException(msg);
                    }

                    if (job.Config?.NotifyRip ?? settings.Value.NotifyRip)
                    {
                        await notifications.NotifyAsync(job, NotificationService.NotifyTitle,
                            $"{job.Title} rip complete. Starting transcode.", ct);
                    }

                    logger.LogInformation("************* Ripping with MakeMKV completed *************");
                    transcodeInPath = makeMkvOutPath;
                    goto afterMakeMkv;
                }

                Track? longestTrack = null;
                foreach (var track in tracks)
                {
                    var length = track.Length ?? 0;
                    track.Process = length >= minLength && length <= maxLength;

                    if (longestTrack is null || length > (longestTrack.Length ?? 0))
                        longestTrack = track;
                }

                if (longestTrack is not null)
                    longestTrack.MainFeature = true;

                foreach (var track in tracks)
                    db.Tracks.Add(track);
                await db.SaveChangesAsync(ct);

                logger.LogInformation("************* Ripping disc with MakeMKV *************");
                job.Stage = RipStage.Identify;
                GuardStage(job, "identify", "Active/VideoInfo", () => job.Status is JobState.Active or JobState.VideoInfo);
                job.Stage = RipStage.Rip;
                job.Status = JobState.VideoRipping;
                job.ProgressMessage = "Starting rip...";
                await db.SaveChangesAsync(ct);
                BroadcastJobUpdate(job);

                string? ripError = null;
                try
                {
                    if (!Directory.Exists(makeMkvOutPath))
                        Directory.CreateDirectory(makeMkvOutPath);

                    var eligibleTracks = tracks.Where(t => t.Process).ToList();
                    var mkvArgs = config?.MkvArgs ?? settings.Value.MkvArgs ?? "";
                    var ripCount = 0;

                    if (settings.Value.TestMode)
                    {
                        var firstTrack = eligibleTracks.FirstOrDefault();
                        if (firstTrack is not null)
                            await makeMkv.RipTrackAsync(job, firstTrack.TrackNumber!, makeMkvOutPath, mkvArgs, minLength, MkvProgress(job, "Ripping track 0", ct), ct);
                        else
                            await makeMkv.RipTrackAsync(job, "0", makeMkvOutPath, mkvArgs, minLength, MkvProgress(job, "Ripping track 0", ct), ct);
                    }
                    else if (config?.MainFeature ?? settings.Value.MainFeature)
                    {
                        var main = tracks.FirstOrDefault(t => t.MainFeature);
                        if (main is not null)
                        {
                            await makeMkv.RipTrackAsync(job, main.TrackNumber!, makeMkvOutPath, mkvArgs, minLength, MkvProgress(job, "Ripping main feature", ct), ct);
                            ripCount = 1;
                        }
                    }
                    else if (maxLength > 99998)
                    {
                        await makeMkv.RipAllTitlesAsync(job, makeMkvOutPath, mkvArgs, minLength, MkvProgress(job, "Ripping all titles", ct), ct);
                        ripCount = eligibleTracks.Count;
                    }
                    else
                    {
                        var trackNum = 0;
                        foreach (var track in eligibleTracks)
                        {
                            trackNum++;
                            await makeMkv.RipTrackAsync(job, track.TrackNumber!, makeMkvOutPath, mkvArgs, minLength, MkvProgress(job, $"Ripping track {trackNum} of {eligibleTracks.Count}", ct), ct);
                            ripCount++;
                        }
                    }

                    logger.LogInformation("Ripped {Count} titles", ripCount);
                }
                catch (Exception mkvError)
                {
                    logger.LogError(mkvError, "Error while running MakeMKV");
                    ripError = mkvError.Message;
                }

                // Match output files to tracks (runs even after partial rip failure)
                if (Directory.Exists(makeMkvOutPath))
                {
                    var dbTracks = await db.Tracks.Where(t => t.JobId == job.Id).ToListAsync(ct);
                    foreach (var file in Directory.EnumerateFiles(makeMkvOutPath, "*.mkv"))
                    {
                        var fileName = Path.GetFileName(file);
                        var fileInfo = new FileInfo(file);

                        var track = dbTracks.FirstOrDefault(t =>
                            !string.IsNullOrEmpty(t.FileName) &&
                            t.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase))
                            ?? dbTracks.FirstOrDefault(t =>
                                !string.IsNullOrEmpty(t.TrackNumber) &&
                                fileName.Contains($"t{int.Parse(t.TrackNumber):D2}"));

                        if (track is not null)
                        {
                            track.FileName = fileName;
                            track.FileSize = fileInfo.Length;
                            track.Ripped = true;
                        }
                    }
                    await db.SaveChangesAsync(ct);

                    if (dbTracks.Count > 0 && !dbTracks.Any(t => t.Ripped))
                    {
                        var msg = ripError is not null
                            ? $"MakeMKV rip failed — no output files: {ripError}"
                            : "MakeMKV rip produced no ripped tracks";
                        logger.LogError(msg);
                        job.Status = JobState.Failure;
                        job.Errors = msg;
                        await db.SaveChangesAsync(ct);
                        throw new InvalidOperationException(msg);
                    }
                }

                // Handle partial failure: some tracks succeeded, record the error
                if (ripError is not null)
                {
                    job.Errors = $"MakeMKV rip errors (partial): {ripError}";
                    job.Status = JobState.Failure;
                    logger.LogWarning("MakeMKV rip completed with partial errors — continuing to transcode succeeded tracks");
                }

                if (job.Config?.NotifyRip ?? settings.Value.NotifyRip)
                {
                    await notifications.NotifyAsync(job, NotificationService.NotifyTitle,
                        $"{job.Title} rip complete. Starting transcode.", ct);
                }

                logger.LogInformation("************* Ripping with MakeMKV completed *************");
                transcodeInPath = makeMkvOutPath;

                job.MarkStageComplete(RipStage.Rip);
                await db.SaveChangesAsync(ct);
                BroadcastJobUpdate(job);
                }
            }

afterMakeMkv:
        // ── 3. Test-mode trim (optional) ──
        if (settings.Value.TestMode && transcodeInPath is not null && Directory.Exists(transcodeInPath))
        {
            logger.LogInformation("Test mode: trimming raw MKV files to 30 seconds");
            foreach (var file in Directory.EnumerateFiles(transcodeInPath, "*.mkv"))
            {
                var tmp = file + ".trimmed";
                var trimResult = await runner.RunAsync("ffmpeg",
                    $"-t 30 -i \"{file}\" -c copy -y \"{tmp}\"", timeoutMs: 60_000, ct: ct);
                if (trimResult.ExitCode == 0 && File.Exists(tmp))
                {
                    File.Delete(file);
                    File.Move(tmp, file);
                }
            }
        }

        // ── 4. Transcode (idempotent) ──
        if (job.IsStageComplete(RipStage.Transcode))
        {
            logger.LogInformation("Stage 'transcode' already completed — skipping transcode");
        }
        else
        {
            await StartTranscodeAsync(job, logFile, transcodeInPath!, transcodeOutPath, protection, ct);
            job.MarkStageComplete(RipStage.Transcode);
            await db.SaveChangesAsync(ct);
            BroadcastJobUpdate(job);
        }

        // ── 5. Finalize: manual title, file moves, Emby, cleanup ──
        job.Stage = RipStage.Finalize;
        job.ProgressMessage = "Finalizing...";
        await db.SaveChangesAsync(ct);
        BroadcastJobUpdate(job);

        logger.LogDebug("Transcode status: [{SkipTranscode}] and MakeMKV Status: [{UseMakeMkv}]",
            job.Config?.SkipTranscode ?? settings.Value.SkipTranscode, useMakeMkv);

        if ((job.Config?.SkipTranscode ?? settings.Value.SkipTranscode) && useMakeMkv)
        {
            DeleteRawFiles(new[] { transcodeOutPath });
            transcodeOutPath = transcodeInPath!;
        }

        logger.LogDebug("Job title manual status: [{TitleManual}]", job.TitleManual);
        if (!string.IsNullOrEmpty(job.TitleManual))
        {
            DeleteRawFiles(new[] { finalDirectory });
            jobTitle = FixJobTitle(job);
            finalDirectory = Path.Combine(job.Config?.CompletedPath ?? settings.Value.CompletedPath!, typeSubFolder, jobTitle);
            job.Path = finalDirectory;
            await db.SaveChangesAsync(ct);
        }

        await db.Entry(job).Collection(j => j.Tracks).LoadAsync(ct);
        await MoveFilesPostAsync(transcodeOutPath, job, ct);

        await ScanEmbyAsync(job, ct);

        SetPermissions(finalDirectory, job);

        DeleteRawFiles(new[] { transcodeInPath, transcodeOutPath, makeMkvOutPath }.OfType<string>().ToArray());

        await NotifyExitAsync(job, ct);

        job.Stage = RipStage.Done;
        job.MarkStageComplete(RipStage.Finalize);
        job.ProgressMessage = null;
        await db.SaveChangesAsync(ct);
        BroadcastJobUpdate(job);

        logger.LogInformation("************* ARM processing complete *************");
        return finalDirectory;
    }

    private async Task StartTranscodeAsync(Job job, string logFile, string rawInPath, string transcodeOutPath, bool protection, CancellationToken ct)
    {
        if (job.Config?.SkipTranscode ?? settings.Value.SkipTranscode)
        {
            logger.LogInformation("Transcoding is disabled, skipping transcode");
            return;
        }

        GuardStage(job, "rip", "VideoRipping", () => job.Status is JobState.VideoRipping);
        job.Stage = RipStage.Transcode;
        job.Status = JobState.TranscodeActive;
        job.ProgressMessage = "Starting transcode...";
        await db.SaveChangesAsync(ct);
        BroadcastJobUpdate(job);

        if (job.Config?.UseFfmpeg ?? settings.Value.UseFfmpeg)
        {
            logger.LogInformation("************* Starting Transcode With FFMPEG *************");
            if (RipWithMkv(job, protection) && (job.Config?.RipMethod ?? settings.Value.RipMethod) == "mkv")
            {
                logger.LogDebug("ffmpeg_mkv: {RawInPath}, {TranscodeOutPath}", rawInPath, transcodeOutPath);
                await ffmpeg.TranscodeMkvAsync(job, rawInPath, transcodeOutPath, TranscodeProgress(job, "Transcoding MKV files", ct), ct);
            }
            else if ((job.VideoType == "movie" || job.VideoType is null) && (job.Config?.MainFeature ?? settings.Value.MainFeature))
            {
                logger.LogDebug("ffmpeg_main_feature: {RawInPath}, {TranscodeOutPath}", rawInPath, transcodeOutPath);
                await ffmpeg.TranscodeMainFeatureAsync(job, rawInPath, transcodeOutPath, TranscodeProgress(job, "Transcoding main feature", ct), ct);
            }
            else
            {
                logger.LogDebug("ffmpeg_all: {RawInPath}, {TranscodeOutPath}", rawInPath, transcodeOutPath);
                await ffmpeg.TranscodeAllAsync(job, rawInPath, transcodeOutPath, TranscodeProgress(job, "Transcoding all tracks", ct), ct);
            }
            logger.LogInformation("************* Finished Transcode With FFMPEG *************");

            if (job.Status != JobState.Failure)
            {
                job.Status = JobState.Active;
                await db.SaveChangesAsync(ct);
            }
        }
        else
        {
            logger.LogInformation("************* Starting Transcode With HandBrake *************");
            if (RipWithMkv(job, protection) && (job.Config?.RipMethod ?? settings.Value.RipMethod) == "mkv")
            {
                logger.LogDebug("handbrake_mkv: {RawInPath}, {TranscodeOutPath}", rawInPath, transcodeOutPath);
                await handBrake.TranscodeMkvAsync(job, rawInPath, transcodeOutPath, TranscodeProgress(job, "Transcoding MKV files", ct), ct);
            }
            else if ((job.VideoType == "movie" || job.VideoType is null) && (job.Config?.MainFeature ?? settings.Value.MainFeature))
            {
                logger.LogDebug("handbrake_main_feature: {RawInPath}, {TranscodeOutPath}", rawInPath, transcodeOutPath);
                await handBrake.TranscodeMainFeatureAsync(job, rawInPath, transcodeOutPath, TranscodeProgress(job, "Transcoding main feature", ct), ct);
            }
            else
            {
                logger.LogDebug("handbrake_all: {RawInPath}, {TranscodeOutPath}", rawInPath, transcodeOutPath);
                await handBrake.TranscodeAllAsync(job, rawInPath, transcodeOutPath, TranscodeProgress(job, "Transcoding all tracks", ct), ct);
            }
            logger.LogInformation("************* Finished Transcode With HandBrake *************");

            if (job.Status != JobState.Failure)
            {
                job.Status = JobState.Active;
                await db.SaveChangesAsync(ct);
            }
        }
    }

    private void RecordStageError(Job job, string stage, string message)
    {
        logger.LogWarning("Stage '{Stage}' error: {Message}", stage, message);
        var entry = $"{stage}:{message}";
        job.StageErrors = job.StageErrors is { } existing ? $"{existing};{entry}" : entry;
    }

    private void GuardStage(Job job, string stage, string expectedStatus, Func<bool> condition)
    {
        if (condition()) return;
        RecordStageError(job, stage, $"Expected status {expectedStatus}, was {job.Status}");
    }

    /// <summary>Fire-and-forget broadcast of job state to all connected UI clients.</summary>
    private void BroadcastJobUpdate(Job job)
    {
        var update = JobUpdate.FromJob(job);
        foreach (var b in broadcasters)
            _ = b.BroadcastJobUpdateAsync(update);
    }

    private IProgress<int> MkvProgress(Job job, string message, CancellationToken ct) =>
        new InlineProgress<int>(pct =>
        {
            job.MakeMkvProgress = pct;
            job.ProgressMessage = message;
            // Progress percent flows over SignalR only — NOT persisted to DB.
            // Stage completions are written atomically by the Conductor on stage transitions.
            BroadcastJobUpdate(job);
        });

    private IProgress<int> TranscodeProgress(Job job, string message, CancellationToken ct) =>
        new InlineProgress<int>(pct =>
        {
            job.TranscodeProgress = pct;
            job.ProgressMessage = message;
            // Progress percent flows over SignalR only — NOT persisted to DB.
            // Stage completions are written atomically by the Conductor on stage transitions.
            BroadcastJobUpdate(job);
        });

    /// <summary>
    /// A simple IProgress&lt;T&gt; implementation that invokes the handler
    /// synchronously on the calling thread, avoiding SynchronizationContext dispatch.
    /// </summary>
    private sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    private async Task NotifyExitAsync(Job job, CancellationToken ct)
    {
        if (!(job.Config?.NotifyTranscode ?? settings.Value.NotifyTranscode))
            return;

        if (!string.IsNullOrEmpty(job.Errors))
        {
            await notifications.NotifyAsync(job, NotificationService.NotifyTitle,
                $" {job.Title} processing completed with errors. Title(s) {job.Errors} failed to complete.", ct);
            logger.LogInformation("Transcoding completed with errors. Title(s) {Errors} failed to complete.", job.Errors);
        }
        else
        {
            await notifications.NotifyAsync(job, NotificationService.NotifyTitle,
                $"{job.Title} processing complete.", ct);
        }
    }

    private async Task MoveFilesPostAsync(string transcodeOutPath, Job job, CancellationToken ct)
    {
        var tracks = job.Tracks.Where(t => t.Ripped).ToList();

        if (job.VideoType == "series")
        {
            foreach (var track in tracks)
                MoveFiles(transcodeOutPath, track.FileName!, job, false);
        }
        else
        {
            foreach (var track in tracks)
            {
                if (tracks.Count == 1)
                {
                    MoveFiles(transcodeOutPath, track.FileName!, job, true);
                }
                else
                {
                    if (track.Source == "MakeMKV" && job.VideoType == "movie")
                    {
                        SkipTranscodeMovie(Directory.GetFiles(transcodeOutPath).Select(Path.GetFileName).Cast<string>().ToList(), job, transcodeOutPath);
                        break;
                    }
                    MoveFiles(transcodeOutPath, track.FileName!, job, track.MainFeature);
                }
            }
        }
    }

    private static bool RipWithMkv(Job currentJob, bool protection)
    {
        var config = currentJob.Config;
        var ripMethod = config?.RipMethod ?? "mkv";
        var skipTranscode = config?.SkipTranscode ?? false;
        var mainFeature = config?.MainFeature ?? true;

        if (currentJob.DiscType == DiscType.Bluray) return true;
        if (currentJob.DiscType == DiscType.Dvd && ripMethod == "mkv") return true;
        if (currentJob.DiscType == DiscType.Dvd && skipTranscode) return true;
        if (protection && currentJob.DiscType == DiscType.Dvd) return true;
        if (ripMethod == "backup_dvd") return true;

        return false;
    }

    private void SkipTranscodeMovie(List<string> files, Job job, string rawPath)
    {
        logger.LogDebug("Videotype: {VideoType}", job.VideoType);

        if (job.VideoType != "movie") return;

        logger.LogDebug("Finding largest file");
        var largestFileName = FindLargestFile(files, rawPath);
        logger.LogDebug("Largest file is: {LargestFile}", largestFileName);

        if (string.IsNullOrEmpty(largestFileName)) return;

        var tempPath = Path.Combine(rawPath, largestFileName);
        try
        {
            var fileInfo = new FileInfo(tempPath);
            if (fileInfo.Length <= 1)
                logger.LogInformation("{RawPath} is empty or very small size. - Folder size: {Size}", rawPath, fileInfo.Length);
        }
        catch { }

        foreach (var file in files)
        {
            if (file == largestFileName)
            {
                MoveFiles(rawPath, file, job, true);
            }
            else
            {
                if (job.Config?.MainFeature ?? settings.Value.MainFeature)
                {
                    logger.LogInformation("MAINFEATURE IS TRUE - Skipping move of {File}", file);
                    continue;
                }

                if (!string.IsNullOrEmpty(job.Config?.ExtrasSub) &&
                    job.Config.ExtrasSub.Equals("none", StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogInformation("Not moving extra: \"{File}\" - Sub folder is not set or named incorrectly", file);
                }
                else
                {
                    MoveFiles(rawPath, file, job, false);
                }
            }
        }
    }

    private void MoveFiles(string basePath, string filename, Job job, bool isMainFeature)
    {
        if (string.IsNullOrEmpty(filename))
        {
            logger.LogInformation("Filename is empty... Skipping");
            return;
        }

        var videoTitle = FixJobTitle(job);
        var moviePath = job.Path;

        if (string.IsNullOrEmpty(moviePath))
        {
            logger.LogWarning("Job path is null");
            return;
        }

        logger.LogInformation("Moving {VideoType} {Filename} to {MoviePath}", job.VideoType, filename, moviePath);

        var extrasPath = job.VideoType != "series" && !string.IsNullOrEmpty(job.Config?.ExtrasSub)
            ? Path.Combine(moviePath, job.Config.ExtrasSub)
            : moviePath;

        EnsureDirectory(moviePath);

        if (isMainFeature)
        {
            var destExt = job.Config?.DestExt ?? settings.Value.DestExt ?? "mp4";
            var movieFile = Path.Combine(moviePath, $"{videoTitle}.{destExt}");
            logger.LogInformation("Track is the Main Title. Moving '{Src}' to '{Dst}'", Path.Combine(basePath, filename), movieFile);
            MoveFileMain(Path.Combine(basePath, filename), movieFile);
        }
        else
        {
            EnsureDirectory(extrasPath);
            logger.LogInformation("Moving '{Src}' to '{Dst}'", Path.Combine(basePath, filename), extrasPath);
            MoveFileMain(Path.Combine(basePath, filename), Path.Combine(extrasPath, filename));
        }
    }

    private static void MoveFileMain(string oldFile, string newFile)
    {
        if (File.Exists(newFile))
            return;

        if (!File.Exists(oldFile))
            return;

        var dir = Path.GetDirectoryName(newFile);
        if (dir is not null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        File.Move(oldFile, newFile);
    }

    internal static string FixJobTitle(Job job)
    {
        if (!string.IsNullOrEmpty(job.Year) && job.Year != "0000")
        {
            if (!string.IsNullOrEmpty(job.TitleManual))
                return $"{job.TitleManual} ({job.Year})";
            return $"{job.Title} ({job.Year})";
        }

        return job.TitleManual ?? job.Title ?? "unknown";
    }

    internal static string ConvertJobType(string? videoType)
    {
        return videoType?.ToLowerInvariant() switch
        {
            "movie" => "movies",
            "series" => "tv",
            _ => "unidentified"
        };
    }

    private string CheckForDupeFolder(bool hasDupes, string hbOutPath, Job job)
    {
        if (EnsureDirectory(hbOutPath))
            return hbOutPath;

        logger.LogInformation("Output directory \"{Path}\" already exists.", hbOutPath);

        var allowDuplicates = job.Config?.AllowDuplicates ?? settings.Value.AllowDuplicates;
        logger.LogDebug("Value of ALLOW_DUPLICATES: {AllowDuplicates}", allowDuplicates);
        logger.LogDebug("Value of have_dupes: {HasDupes}", hasDupes);

        if (allowDuplicates || !hasDupes)
        {
            hbOutPath = hbOutPath + "_" + job.Id;
            EnsureDirectory(hbOutPath);
            return hbOutPath;
        }

        logger.LogInformation("Duplicate rips are disabled.");
        throw new InvalidOperationException("Duplicate rips are disabled");
    }

    private static string FindLargestFile(List<string> files, string mkvOutPath)
    {
        var largestFileName = "";
        long largestSize = -1;

        foreach (var file in files)
        {
            var fullPath = Path.Combine(mkvOutPath, file);
            try
            {
                var fileInfo = new FileInfo(fullPath);
                if (fileInfo.Exists && fileInfo.Length > largestSize)
                {
                    largestSize = fileInfo.Length;
                    largestFileName = file;
                }
            }
            catch { }
        }

        return largestFileName;
    }

    private async Task ScanEmbyAsync(Job job, CancellationToken ct)
    {
        var config = job.Config;
        if (config is null || !config.EmbyRefresh)
        {
            logger.LogInformation("EMBY_REFRESH config parameter is false. Skipping emby scan.");
            return;
        }

        var url = $"http://{config.EmbyServer}:{config.EmbyPort}/Library/Refresh?api_key={config.EmbyApiKey}";
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var response = await client.PostAsync(url, null, ct);
            response.EnsureSuccessStatusCode();
            logger.LogInformation("Emby Library Scan request successful");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Emby Library Scan request failed");
        }
    }

    private void SetPermissions(string directoryToTraverse, Job job)
    {
        if (job.Config is null) return;

        try
        {
            var chmodValue = Convert.ToInt32("777", 8);
            logger.LogInformation("Setting permissions to: {ChmodValue} on: {Dir}", chmodValue, directoryToTraverse);
            SetUnixPermissionsRecursive(directoryToTraverse, chmodValue);
            logger.LogInformation("Permissions set successfully");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Permissions setting failed");
        }
    }

    private void DeleteRawFiles(string[] dirList)
    {
        foreach (var rawFolder in dirList)
        {
            try
            {
                if (Directory.Exists(rawFolder))
                {
                    logger.LogInformation("Removing raw path - {RawFolder}", rawFolder);
                    Directory.Delete(rawFolder, recursive: true);
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "No raw files found to delete in {RawFolder}", rawFolder);
            }
        }
    }

    private static void SetUnixPermissionsRecursive(string path, int mode)
    {
        if (Directory.Exists(path))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(path))
            {
                SetUnixPermissionsRecursive(entry, mode);
            }
        }

#pragma warning disable CA1416
        File.SetUnixFileMode(path, (UnixFileMode)mode);
#pragma warning restore CA1416
    }

    private static bool EnsureDirectory(string path)
    {
        if (Directory.Exists(path))
            return false;

        Directory.CreateDirectory(path);
        return true;
    }
}

