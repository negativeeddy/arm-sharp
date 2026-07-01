using ArmMedia.Core.Abstractions;
using ArmMedia.Core.Models;
using ArmRipper.Core.Configuration;
using ArmRipper.Core.Infrastructure;
using ArmRipper.Core.Infrastructure.Data;
using ArmRipper.Core.Models;
using ArmRipper.Core.Notifications;
using System.Collections.Concurrent;
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
    IEnumerable<INotificationBroadcaster> broadcasters,
    IIdentifyService identifyService,
    IDiscDbMappingService discDbMappingService,
    ITrackMapperService trackMapperService,
    IEpisodeIdentificationOrchestrator? episodeOrchestrator = null) : IArmRipperService
{
    private readonly ILogger logger = loggerFactory.CreateLogger("ArmRipperService");
    private static readonly TimeSpan ProgressBroadcastInterval = TimeSpan.FromMilliseconds(200);
    private readonly ConcurrentDictionary<string, (int Percent, DateTime LastBroadcastUtc)> progressBroadcastState = new();
    public async Task<string> RipVisualMediaAsync(Job job, string logFile, bool hasDupes, bool protection, CancellationToken ct = default)
    {
        // ── 1. Compute paths ──
        var typeSubFolder = ConvertJobType(job.VideoType);
        var jobTitle = FixJobTitle(job);

        var transcodeOutPath = Path.Combine(job.Config?.TranscodePath ?? ArmPaths.GetTranscodePath(settings.Value), typeSubFolder, jobTitle);
        var finalDirectory = Path.Combine(job.Config?.CompletedPath ?? ArmPaths.GetCompletedPath(settings.Value), typeSubFolder, jobTitle);

        job.Stage ??= RipStage.Setup;
        job.Stage = RipStage.Identify;  // transition stage
        job.ProgressMessage ??= "Preparing to rip...";
        await db.SaveChangesAsync(ct);
        await BroadcastJobUpdateAsync(job);

        transcodeOutPath = CheckForDupeFolder(hasDupes, transcodeOutPath, job);
        finalDirectory = CheckForDupeFolder(hasDupes, finalDirectory, job);

        job.Path = finalDirectory;
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Processing files to: {TranscodeOutPath}", transcodeOutPath);

        var makeMkvOutPath = Path.Combine(job.Config?.RawPath ?? ArmPaths.GetRawPath(settings.Value), jobTitle);
        var transcodeInPath = job.DevPath;
        var useMakeMkv = RipWithMkv(job, protection);

        logger.LogDebug("Using MakeMKV: {UseMakeMkv}", useMakeMkv);

        // ── 2. MakeMKV rip (idempotent) ──
        if (useMakeMkv)
        {
            transcodeInPath = await PrepareTranscodeInputPathAsync(job, jobTitle, makeMkvOutPath, ct);
        }

        // ── 2b. Eject the disc now that the rip is done — transcode uses files only.
        //     If AutoEject is disabled in config, this is a no-op.
        await identifyService.EjectAsync(job, ct);
        job.Ejected = true;
        await db.SaveChangesAsync(ct);

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

        // ── 4. TV episode identification (runs after rip, before transcode) ──
        // Run the full provider chain (DiscDb → DvdCompare → FileBot → TMDB →
        // TVDB → OMDB) to identify TV episode assignments for naming, so the
        // transcode and move steps can use episode numbers/titles.
        await db.Entry(job).Collection(j => j.Tracks).LoadAsync(ct);
        if (episodeOrchestrator is not null &&
            (job.VideoType == "series" || job.VideoType == "tv"))
        {
            await RunEpisodeIdentificationAsync(job, makeMkvOutPath, ct);
        }

        // ── 5. Transcode (idempotent) ──
        if (job.IsStageComplete(RipStage.Transcode))
        {
            logger.LogInformation("Stage 'transcode' already completed — skipping transcode");
        }
        else
        {
            await StartTranscodeAsync(job, logFile, transcodeInPath!, transcodeOutPath, protection, ct);
            job.MarkStageComplete(RipStage.Transcode);
            await db.SaveChangesAsync(ct);
            await BroadcastJobUpdateAsync(job);
        }

        // ── 6. Finalize: manual title, file moves, Emby, cleanup ──
        job.Stage = RipStage.Finalize;
        job.ProgressMessage = "Finalizing...";
        await db.SaveChangesAsync(ct);
        await BroadcastJobUpdateAsync(job);

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
            finalDirectory = Path.Combine(job.Config?.CompletedPath ?? ArmPaths.GetCompletedPath(settings.Value), typeSubFolder, jobTitle);
            // Re-apply dupe folder suffix — CheckForDupeFolder already determined a suffix
            // was needed, but the path recalculation above dropped it.
            finalDirectory = CheckForDupeFolder(hasDupes, finalDirectory, job);
            job.Path = finalDirectory;
            await db.SaveChangesAsync(ct);
        }

        await MoveFilesPostAsync(transcodeOutPath, job, ct);

        await ScanEmbyAsync(job, ct);

        await SetPermissionsAsync(job.Path ?? finalDirectory, job, ct);

        var delRaw = job.Config?.DelRawFiles ?? settings.Value.DelRawFiles;
        if (delRaw)
        {
            DeleteRawFiles(new[] { transcodeInPath, transcodeOutPath, makeMkvOutPath }.OfType<string>().ToArray());
        }
        else
        {
            logger.LogInformation("DelRawFiles is disabled — keeping raw files at {Paths}",
                string.Join(", ", new[] { transcodeInPath, transcodeOutPath, makeMkvOutPath }.OfType<string>()));
        }

        await NotifyExitAsync(job, ct);

        job.Stage = RipStage.Done;
        job.MarkStageComplete(RipStage.Finalize);
        job.ProgressMessage = null;
        await db.SaveChangesAsync(ct);
        await BroadcastJobUpdateAsync(job);

        logger.LogInformation("************* ARM processing complete *************");
        return job.Path ?? finalDirectory;
    }

    private async Task<string?> PrepareTranscodeInputPathAsync(Job job, string jobTitle, string makeMkvOutPath, CancellationToken ct)
    {
        if (job.IsStageComplete(RipStage.Rip))
        {
            logger.LogInformation("Stage 'rip' already completed — skipping MakeMKV rip");
            return makeMkvOutPath;
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
            return makeMkvOutPath;
        }

        logger.LogInformation("************* Getting track info from MakeMKV *************");

        var config = job.Config;
        var minLengthCfg = config?.MinLength ?? settings.Value.MinLength;
        var maxLength = config?.MaxLength ?? settings.Value.MaxLength;

        // When DiscDb is enabled, pass infoMinLength=0 so MakeMKV reports ALL tracks,
        // including short extras that may match DiscDb entries. Our own minLengthCfg
        // and DiscDb promotion logic will handle filtering and promotion.
        var infoMinLength = settings.Value.DiscDbEnabled ? 0 : (int?)null;
        var tracks = await makeMkv.GetTrackInfoWithCacheAsync(job, jobTitle, infoMinLength, ct);

        // Encrypted BDs often return 0 tracks from info; rip all titles directly
        if (tracks.Count == 0 && job.DiscType is DiscType.Bluray or DiscType.Dvd)
        {
            job.Stage = RipStage.Identify;
            GuardStage(job, "identify", "Active/VideoInfo", () => job.Status is JobState.Active or JobState.VideoInfo);
            job.Stage = RipStage.Rip;
            job.Status = JobState.VideoRipping;
            job.ProgressMessage = "Starting rip...";
            await db.SaveChangesAsync(ct);
            await BroadcastJobUpdateAsync(job);

            if (!Directory.Exists(makeMkvOutPath))
                Directory.CreateDirectory(makeMkvOutPath);

            var mkvArgs = config?.MkvArgs ?? settings.Value.MkvArgs ?? "";
            await makeMkv.RipAllTitlesAsync(job, makeMkvOutPath, mkvArgs, minLengthCfg, MkvProgress(job, "Ripping all titles", ct), ct);
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
            return makeMkvOutPath;
        }

        Track? longestTrack = null;
        foreach (var track in tracks)
        {
            var length = track.Length ?? 0;
            track.Process = length >= minLengthCfg && length <= maxLength;

            if (longestTrack is null || length > (longestTrack.Length ?? 0))
                longestTrack = track;
        }

        if (longestTrack is not null)
            longestTrack.MainFeature = true;

        foreach (var track in tracks)
            db.Tracks.Add(track);
        await db.SaveChangesAsync(ct);

        // ── DiscDb track mapping: promote short tracks that have a DiscDb match ──
        if (settings.Value.DiscDbEnabled && !string.IsNullOrEmpty(job.DiscDbHash))
        {
            logger.LogInformation(
                "DiscDb: hash {Hash} present, attempting track mapping for job {JobId}",
                job.DiscDbHash[..Math.Min(8, job.DiscDbHash.Length)], job.Id);

            var discDbMapping = await discDbMappingService.GetCachedMappingAsync(job.DiscDbHash, ct);
            if (discDbMapping is not null)
            {
                _ = await trackMapperService.MapTracksAsync(job, discDbMapping, ct);

                // Reload tracks from DB — the mapper modified DB-tracked instances,
                // not the local 'tracks' list, so Process/EpisodeTitle are stale here.
                var freshTracks = await db.Tracks.Where(t => t.JobId == job.Id).ToListAsync(ct);

                // Sync Process flag back to local list for the rip loop below,
                // and promote any short track that got a DiscDb match.
                var promoted = 0;
                foreach (var fresh in freshTracks)
                {
                    var local = tracks.FirstOrDefault(t => t.Id == fresh.Id);
                    if (local is not null)
                    {
                        local.EpisodeTitle = fresh.EpisodeTitle;
                        local.ContentType = fresh.ContentType;
                        local.EpisodeNumber = fresh.EpisodeNumber;
                        local.TrackSeasonNumber = fresh.TrackSeasonNumber;
                        local.DiscDbItemSlug = fresh.DiscDbItemSlug;

                        if (!local.Process && !string.IsNullOrEmpty(fresh.EpisodeTitle))
                        {
                            local.Process = true;
                            promoted++;
                        }
                    }
                }

                if (promoted > 0)
                {
                    // Persist the promoted Process flags
                    foreach (var t in tracks.Where(t => t.Process))
                        db.Entry(t).Property(x => x.Process).IsModified = true;
                    await db.SaveChangesAsync(ct);
                    await BroadcastJobUpdateAsync(job);
                    logger.LogInformation(
                        "DiscDb: promoted {Promoted} short track(s) to Process=true for job {JobId}",
                        promoted, job.Id);
                }
                else
                {
                    logger.LogDebug(
                        "DiscDb: mapping ran but no short tracks were promoted for job {JobId}",
                        job.Id);
                }
            }
            else
            {
                logger.LogInformation(
                    "DiscDb: mapping not found for hash {Hash}... (cache miss or API returned no match)",
                    job.DiscDbHash.Length >= 8 ? job.DiscDbHash[..8] : job.DiscDbHash);
            }
        }
        else
        {
            logger.LogInformation(
                "DiscDb: skipping track mapping (enabled={Enabled}, hash={Hash}) for job {JobId}",
                settings.Value.DiscDbEnabled, job.DiscDbHash ?? "(null)", job.Id);
        }

        logger.LogInformation("************* Ripping disc with MakeMKV *************");
        job.Stage = RipStage.Identify;
        GuardStage(job, "identify", "Active/VideoInfo", () => job.Status is JobState.Active or JobState.VideoInfo);
        job.Stage = RipStage.Rip;
        job.Status = JobState.VideoRipping;
        job.ProgressMessage = "Starting rip...";
        await db.SaveChangesAsync(ct);
        await BroadcastJobUpdateAsync(job);

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
                    await makeMkv.RipTrackAsync(job, firstTrack.TrackNumber!, makeMkvOutPath, mkvArgs, minLengthCfg, MkvProgress(job, "Ripping track 0", ct), ct);
                else
                    await makeMkv.RipTrackAsync(job, "0", makeMkvOutPath, mkvArgs, minLengthCfg, MkvProgress(job, "Ripping track 0", ct), ct);
            }
            else if (config?.MainFeature ?? settings.Value.MainFeature)
            {
                // MainFeature mode: only rip the single longest track.
                // DiscDb metadata mapping still runs (for poster, title, etc.) but
                // promoted extras are NOT ripped in this mode.
                var main = tracks.FirstOrDefault(t => t.MainFeature);
                if (main is not null)
                {
                    await makeMkv.RipTrackAsync(job, main.TrackNumber!, makeMkvOutPath, mkvArgs, minLengthCfg, MkvProgress(job, "Ripping main feature", ct), ct);
                    ripCount = 1;
                }
            }
            else if (maxLength > 99998 && eligibleTracks.All(t => string.IsNullOrEmpty(t.EpisodeTitle)))
            {
                // Fast path: rip everything >= minLength in a single MakeMKV pass.
                // Only safe when NO tracks have been DiscDb-promoted (no EpisodeTitle),
                // otherwise individual iteration is needed to respect Process flags.
                await makeMkv.RipAllTitlesAsync(job, makeMkvOutPath, mkvArgs, minLengthCfg, MkvProgress(job, "Ripping all titles", ct), ct);
                ripCount = eligibleTracks.Count;
            }
            else
            {
                var trackNum = 0;
                foreach (var track in eligibleTracks)
                {
                    trackNum++;
                    // DiscDb-promoted tracks (with EpisodeTitle) may be shorter than the
                    // configured minLength — we already decided to rip them, so tell MakeMKV
                    // not to filter them out by passing minLength=0.
                    var trackMinLength = !string.IsNullOrEmpty(track.EpisodeTitle) ? 0 : minLengthCfg;
                    await makeMkv.RipTrackAsync(job, track.TrackNumber!, makeMkvOutPath, mkvArgs, trackMinLength, MkvProgress(job, $"Ripping track {trackNum} of {eligibleTracks.Count}", ct), ct);
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

        job.MarkStageComplete(RipStage.Rip);
        await db.SaveChangesAsync(ct);
        await BroadcastJobUpdateAsync(job);

        return makeMkvOutPath;
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
        await BroadcastJobUpdateAsync(job);

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

    /// <summary>Fire-and-forget variant for use inside synchronous IProgress callbacks.</summary>
    private void BroadcastJobUpdateFireAndForget(Job job)
    {
        var update = JobUpdate.FromJob(job);
        foreach (var b in broadcasters)
        {
            try
            {
                _ = b.BroadcastJobUpdateAsync(update).ContinueWith(t =>
                {
                    if (t.Exception is not null)
                        logger.LogWarning(t.Exception, "Broadcast failed for job {JobId}", job.Id);
                }, TaskContinuationOptions.OnlyOnFaulted);
            }
            catch { }
        }
    }

    private IProgress<int> MkvProgress(Job job, string message, CancellationToken ct) =>
        new InlineProgress<int>(pct =>
        {
            job.MakeMkvProgress = pct;
            job.ProgressMessage = message;
            // Progress percent flows over SignalR only — NOT persisted to DB.
            // Stage completions are written atomically by the Conductor on stage transitions.
            if (ShouldBroadcastProgress(job, "mkv", pct))
                BroadcastJobUpdateFireAndForget(job);
        });

    private IProgress<int> TranscodeProgress(Job job, string message, CancellationToken ct) =>
        new InlineProgress<int>(pct =>
        {
            job.TranscodeProgress = pct;
            job.ProgressMessage = message;
            // Progress percent flows over SignalR only — NOT persisted to DB.
            // Stage completions are written atomically by the Conductor on stage transitions.
            if (ShouldBroadcastProgress(job, "transcode", pct))
                BroadcastJobUpdateFireAndForget(job);
        });

    private bool ShouldBroadcastProgress(Job job, string progressType, int percent)
    {
        var key = $"{job.Id}:{progressType}";
        var now = DateTime.UtcNow;
        var force = percent is <= 0 or >= 100;

        while (true)
        {
            if (!progressBroadcastState.TryGetValue(key, out var current))
            {
                if (progressBroadcastState.TryAdd(key, (percent, now)))
                    return true;
                continue;
            }

            if (!force && percent == current.Percent)
                return false;

            if (!force && now - current.LastBroadcastUtc < ProgressBroadcastInterval)
                return false;

            if (progressBroadcastState.TryUpdate(key, (percent, now), current))
                return true;
        }
    }

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

    /// <summary>
    /// Runs the ArmMedia TV episode identification pipeline and merges
    /// results back into the job's tracks (EpisodeNumber, EpisodeTitle, etc.).
    /// </summary>
    private async Task RunEpisodeIdentificationAsync(
        Job job, string makeMkvOutPath, CancellationToken ct)
    {
        try
        {
            var tracks = job.Tracks.Where(t => t.Ripped).OrderBy(t => t.TrackNumberInt ?? 0).ToList();
            if (tracks.Count == 0)
                return;

            var trackContexts = tracks.Select(t =>
            {
                var rawProps = new Dictionary<string, string>();
                if (!string.IsNullOrEmpty(t.FileName))
                    rawProps["FileName"] = t.FileName;
                if (!string.IsNullOrEmpty(t.TrackNumber))
                    rawProps["TrackNumber"] = t.TrackNumber;

                return new TrackContext
                {
                    TrackIndex    = t.TrackNumberInt ?? 0,
                    Duration      = TimeSpan.FromSeconds(t.Length ?? 0),
                    SizeBytes     = t.FileSize ?? 0,
                    ChapterCount  = t.Chapters,
                    DiscDbTrackId = t.DiscDbItemSlug,
                    RawProperties = rawProps
                };
            }).ToList().AsReadOnly();

            var discId = job.DiscDbHash ?? job.Label ?? job.DevPath ?? "unknown";
            var season = job.SeasonNumber ?? 1;

            var ctx = new DiscContext
            {
                DiscId      = discId,
                SeriesTitle = CleanSeriesTitle(job.Title ?? job.Label ?? "Unknown"),
                Season      = season,
                Tracks      = trackContexts,
                DiscDbHint  = makeMkvOutPath,  // FileBot CLI uses this for raw file path
                DiscNumber  = ParseDiscNumber(job.Label)
            };

            logger.LogInformation(
                "[ArmMedia] Running episode identification for '{Title}' S{Season} (disc {Disc}, {Count} tracks)...",
                ctx.SeriesTitle, ctx.Season, ctx.DiscNumber, ctx.Tracks.Count);

            var episodeMap = await episodeOrchestrator!.IdentifyAsync(ctx, ct);

            // Merge results back into job tracks
            foreach (var mapped in episodeMap.Tracks)
            {
                var track = tracks.FirstOrDefault(t => t.TrackNumberInt == mapped.TrackIndex);
                if (track is not null)
                {
                    track.EpisodeNumber   = mapped.Episodes.FirstOrDefault();
                    track.EpisodeTitle    = mapped.Title;
                    track.TrackSeasonNumber = mapped.Season;

                    if (!string.IsNullOrEmpty(mapped.WinningProvider))
                    {
                        logger.LogDebug(
                            "[ArmMedia] Track {Track} → S{Season}E{Ep} '{Title}' ({Provider})",
                            track.TrackNumber, mapped.Season,
                            mapped.Episodes.FirstOrDefault(), track.EpisodeTitle,
                            mapped.WinningProvider);
                    }
                }
            }

            await db.SaveChangesAsync(ct);
            await BroadcastJobUpdateAsync(job);

            logger.LogInformation(
                "[ArmMedia] Episode identification complete. {Count} tracks mapped.",
                episodeMap.Tracks.Count);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "[ArmMedia] Episode identification failed for job {JobId}; falling back to positional naming.",
                job.Id);
        }
    }

    private async Task MoveFilesPostAsync(string transcodeOutPath, Job job, CancellationToken ct)
    {
        var tracks = job.Tracks.Where(t => t.Ripped).ToList();

        // ── Positional fallback for TV series without DiscDb episode mapping ──
        // When VideoType is series/tv but tracks have no EpisodeNumber assigned
        // (e.g., DiscDb had no matching record), assign sequential episode numbers
        // based on physical track order so output files get proper SxxExx names.
        // Parses disc number from the label (e.g., "_D2" → disc 2) so multi-disc
        // sets don't restart at episode 1 on every disc.
        if (job.VideoType == "series" || job.VideoType == "tv")
        {
            int discNumber = ParseDiscNumber(job.Label);

            // Count eligible tracks (those without EpisodeNumber, skipping
            // sub-30-second items that are likely studio logos)
            var eligible = tracks
                .Where(t => t.EpisodeNumber is null)
                .OrderBy(t => t.TrackNumberInt ?? 0)
                .ToList();

            // Let short (<30s) tracks pass through without an episode number;
            // they'll be handled as extras by the MoveFiles routing logic.
            var actualEpisodes = eligible
                .Where(t => (t.Length ?? int.MaxValue) >= 30)
                .ToList();

            int startEpisode = ((discNumber - 1) * actualEpisodes.Count) + 1;

            logger.LogInformation(
                "Positional fallback: disc {Disc}, {Count} eligible tracks, starting at episode {StartEp}",
                discNumber, actualEpisodes.Count, startEpisode);

            int ep = startEpisode;
            foreach (var t in eligible)
            {
                // Only assign episode numbers to tracks >= 30 seconds.
                // Short tracks (logos, warnings) keep EpisodeNumber=null and
                // will be handled as unnamed extras by the move logic.
                if ((t.Length ?? int.MaxValue) >= 30)
                {
                    t.EpisodeNumber = ep;
                    logger.LogDebug(
                        "Positional fallback: track {TrackNum} → episode {Episode}",
                        t.TrackNumber ?? t.FileName, ep);
                    ep++;
                }
            }
        }

        foreach (var track in tracks)
        {
            if (tracks.Count == 1)
            {
                MoveFiles(transcodeOutPath, track.FileName!, job, true, track);
            }
            else
            {
                if (track.Source == "MakeMKV" && job.VideoType == "movie")
                {
                    SkipTranscodeMovie(Directory.GetFiles(transcodeOutPath).Select(Path.GetFileName).Cast<string>().ToList(), job, transcodeOutPath);
                    break;
                }
                MoveFiles(transcodeOutPath, track.FileName!, job, track.MainFeature, track);
            }
        }

        // ── Update job.Path for TV series: files now live under
        //     {completed}/tv/Series Name/Season XX/ instead of the flat
        //     {completed}/tv/DISC_LABEL/ directory that was set at startup.
        //     The Conductor uses job.Path for the final output verification.
        if (job.VideoType == "series" || job.VideoType == "tv")
        {
            var episodeTrack = tracks.FirstOrDefault(t => t.EpisodeNumber is not null);
            if (episodeTrack is not null)
            {
                var cleanSeries = CleanSeriesTitle(job.Title ?? "Unknown Series");
                var season = episodeTrack.TrackSeasonNumber ?? job.SeasonNumber ?? 1;
                var completedBase = job.Config?.CompletedPath ?? ArmPaths.GetCompletedPath(settings.Value);
                job.Path = Path.Combine(completedBase, "tv", SanitizeFileName(cleanSeries));
                logger.LogInformation("Updated job path to series directory: {Path}", job.Path);
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

        // Build a lookup from filename to Track so we can pass DiscDb metadata
        var trackByFileName = job.Tracks
            .Where(t => !string.IsNullOrEmpty(t.FileName))
            .ToDictionary(t => t.FileName!, StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            trackByFileName.TryGetValue(file, out var track);

            if (file == largestFileName)
            {
                MoveFiles(rawPath, file, job, true, track);
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
                    MoveFiles(rawPath, file, job, false, track);
                }
            }
        }
    }

    private void MoveFiles(string basePath, string filename, Job job, bool isMainFeature, Track? track = null)
    {
        if (string.IsNullOrEmpty(filename))
        {
            logger.LogInformation("Filename is empty... Skipping");
            return;
        }

        var moviePath = job.Path;

        if (string.IsNullOrEmpty(moviePath))
        {
            logger.LogWarning("Job path is null");
            return;
        }

        logger.LogInformation("Moving {VideoType} {Filename} to {MoviePath}", job.VideoType, filename, moviePath);

        // ── TV episode naming (DiscDb-mapped) ──
        var useEpisodeNaming = track?.EpisodeNumber is not null &&
                               (job.VideoType == "series" || job.VideoType == "tv");

        if (useEpisodeNaming)
        {
            var season = track!.TrackSeasonNumber ?? job.SeasonNumber ?? 1;
            var episode = track.EpisodeNumber!.Value;

            // ── Plex / Jellyfin convention: Series Name / Season XX / SxxExx - Title.ext ──
            var cleanSeries = CleanSeriesTitle(job.Title ?? "Unknown Series");
            var completedBase = job.Config?.CompletedPath ?? ArmPaths.GetCompletedPath(settings.Value);
            var seriesDir = Path.Combine(completedBase, "tv", SanitizeFileName(cleanSeries));
            var seasonDir = Path.Combine(seriesDir, $"Season {season:D2}");

            var destExt = job.Config?.DestExt ?? settings.Value.DestExt ?? "mp4";
            var episodeTitle = !string.IsNullOrEmpty(track.EpisodeTitle)
                ? $" - {SanitizeFileName(track.EpisodeTitle)}"
                : "";

            var seriesFileName = SanitizeFileName(cleanSeries);
            var episodeFile = Path.Combine(seasonDir,
                $"{seriesFileName} - S{season:D2}E{episode:D2}{episodeTitle}.{destExt}");

            EnsureDirectory(seasonDir);
            logger.LogInformation("Track is a TV episode. Moving '{Src}' to '{Dst}'",
                Path.Combine(basePath, filename), episodeFile);
            MoveFileMain(Path.Combine(basePath, filename), episodeFile, logger);
            return;
        }

        // ── Extras routing by content type (DiscDb-mapped) ──
        var contentType = track?.ContentType;
        var useContentBasedExtras = !string.IsNullOrEmpty(contentType) &&
                                    contentType != "main" &&
                                    contentType != "unknown";

        if (useContentBasedExtras)
        {
            var extrasSubFolder = GetExtrasSubFolder(contentType!, job);
            var extrasPath = Path.Combine(moviePath, extrasSubFolder);
            EnsureDirectory(extrasPath);

            // Use DiscDb-mapped title for the filename (e.g. "Backstage Pass With Lindsay Lohan.mkv")
            var targetName = !string.IsNullOrEmpty(track?.EpisodeTitle)
                ? SanitizeFileName(track!.EpisodeTitle) + Path.GetExtension(filename)
                : filename;

            logger.LogInformation("Moving extra (type={ContentType}) '{Src}' to '{Dst}'",
                contentType, Path.Combine(basePath, filename), Path.Combine(extrasPath, targetName));
            MoveFileMain(Path.Combine(basePath, filename), Path.Combine(extrasPath, targetName), logger);
            return;
        }

        // ── Standard movie/series handling (no episode mapping) ──
        var videoTitle = FixJobTitle(job);

        EnsureDirectory(moviePath);

        if (isMainFeature)
        {
            var destExt = job.Config?.DestExt ?? settings.Value.DestExt ?? "mp4";
            // Use DiscDb title suffix when the track has a specific name (e.g. "Freaky Friday Widescreen")
            var featureTitle = !string.IsNullOrEmpty(track?.EpisodeTitle) &&
                               !track.EpisodeTitle.Contains(videoTitle, StringComparison.OrdinalIgnoreCase)
                ? $"{videoTitle} - {SanitizeFileName(track.EpisodeTitle)}"
                : videoTitle;
            var movieFile = Path.Combine(moviePath, $"{featureTitle}.{destExt}");
            logger.LogInformation("Track is the Main Title. Moving '{Src}' to '{Dst}'", Path.Combine(basePath, filename), movieFile);
            MoveFileMain(Path.Combine(basePath, filename), movieFile, logger);
        }
        else
        {
            var extrasPath = job.VideoType != "series" && !string.IsNullOrEmpty(job.Config?.ExtrasSub)
                ? Path.Combine(moviePath, job.Config.ExtrasSub)
                : moviePath;

            EnsureDirectory(extrasPath);

            // Use DiscDb-mapped title for non-main features (e.g. "Freaky Friday Fullscreen")
            var targetName = !string.IsNullOrEmpty(track?.EpisodeTitle)
                ? SanitizeFileName(track!.EpisodeTitle) + Path.GetExtension(filename)
                : filename;

            logger.LogInformation("Moving '{Src}' to '{Dst}'", Path.Combine(basePath, filename), extrasPath);
            MoveFileMain(Path.Combine(basePath, filename), Path.Combine(extrasPath, targetName), logger);
        }
    }

    /// <summary>
    /// Maps TheDiscDb content type to the appropriate extras subfolder for Plex or Jellyfin.
    /// Plex uses type-specific folders; Jellyfin groups all extras under a single 'Extras/' folder.
    /// </summary>
    private static string GetExtrasSubFolder(string contentType, Job job)
    {
        var extrasSetting = job.Config?.ExtrasSub;
        var isJellyfin = !string.IsNullOrEmpty(extrasSetting) &&
                         extrasSetting.Equals("jellyfin", StringComparison.OrdinalIgnoreCase);

        if (isJellyfin)
        {
            // Jellyfin: single Extras/ folder for all supplementary content
            return "Extras";
        }

        // Plex-style: type-specific subfolders
        return contentType.ToLowerInvariant() switch
        {
            "trailer" => "Trailers",
            "featurette" => "Featurettes",
            "deleted_scene" or "deleted" => "Deleted Scenes",
            "behind_the_scenes" or "behindthescenes" => "Behind The Scenes",
            "interview" => "Interviews",
            "short" => "Shorts",
            _ => "Extras"
        };
    }

    /// <summary>Removes characters that are invalid in filenames.</summary>
    public static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(name.Where(ch => !invalid.Contains(ch)));
    }

    /// <summary>
    /// Parses the 1-based disc number from a disc label.
    /// Handles formats like <c>_D1</c>, <c>D2</c>, <c>_DISC3</c>, <c>DISC4</c>.
    /// Returns 1 when no disc suffix is found.
    /// </summary>
    public static int ParseDiscNumber(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
            return 1;

        // Match _D<num> or DISC<num> at the end of the label (case-insensitive)
        var match = System.Text.RegularExpressions.Regex.Match(
            label, @"[_\s]D(?:ISC)?(\d+)$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (match.Success && int.TryParse(match.Groups[1].Value, out var d) && d > 0)
            return d;

        return 1;
    }

    /// <summary>
    /// Converts a raw disc label or title to a clean human-readable series name
    /// suitable for Plex / Jellyfin folder and file naming.
    /// </summary>
    /// <param name="raw">The raw disc label (e.g. "MY_NAME_IS_EARL_S1_D1") or title.</param>
    /// <returns>A clean, human-readable series name (e.g. "My Name Is Earl").</returns>
    public static string CleanSeriesTitle(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "Unknown Series";

        // Strip year suffix: "My Name Is Earl (2005–2009)" → "My Name Is Earl"
        var result = System.Text.RegularExpressions.Regex.Replace(
            raw.Trim(), @"\s*\([^)]*\d{4}.*\)$", "");

        // Strip season/disc suffix: "MY_NAME_IS_EARL_S1_D1" → "MY_NAME_IS_EARL"
        result = System.Text.RegularExpressions.Regex.Replace(
            result, @"[_\s][Ss]\d+[_\s][Dd](?:ISC)?\d+$", "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // Replace underscores with spaces
        result = result.Replace('_', ' ').Trim();

        // If the result is all-uppercase with no lowercase letters (disc label),
        // convert to title case using CultureInfo.
        if (result.Length > 0 && !result.Any(char.IsLower) && result.Any(char.IsUpper))
        {
            result = System.Globalization.CultureInfo.CurrentCulture.TextInfo
                .ToTitleCase(result.ToLowerInvariant());
        }

        return string.IsNullOrWhiteSpace(result) ? "Unknown Series" : result;
    }

    private static void MoveFileMain(string oldFile, string newFile, ILogger? logger = null)
    {
        if (!File.Exists(oldFile))
            return;

        var dir = Path.GetDirectoryName(newFile);
        if (dir is not null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        if (File.Exists(newFile))
        {
            logger?.LogWarning("Destination already exists — skipping move. Source file will be cleaned up: {Src} -> {Dst}", oldFile, newFile);
            return;
        }

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

    private async Task SetPermissionsAsync(string directoryToTraverse, Job job, CancellationToken ct)
    {
        if (!settings.Value.SetMediaPermissions)
        {
            logger.LogInformation("SET_MEDIA_PERMISSIONS is disabled — skipping permission changes");
            return;
        }

        try
        {
            // ── chmod ──
            var chmodString = settings.Value.ChmodValue ?? "777";
            var chmodValue = Convert.ToInt32(chmodString, 8);
            logger.LogInformation("Setting permissions to: {ChmodValue} on: {Dir}", chmodValue, directoryToTraverse);
            SetUnixPermissionsRecursive(directoryToTraverse, chmodValue);
            logger.LogInformation("Permissions set successfully");

            // ── chown ──
            if (settings.Value.SetMediaOwner)
            {
                var chownUser = settings.Value.ChownUser;
                if (string.IsNullOrEmpty(chownUser))
                    chownUser = Environment.GetEnvironmentVariable("ARM_UID") ?? "arm";

                var chownGroup = settings.Value.ChownGroup;
                if (string.IsNullOrEmpty(chownGroup))
                    chownGroup = Environment.GetEnvironmentVariable("ARM_GID") ?? "arm";

                logger.LogInformation("Setting owner to {User}:{Group} on: {Dir}", chownUser, chownGroup, directoryToTraverse);
                var chownResult = await runner.RunAsync(
                    "chown", $"-R {chownUser}:{chownGroup} \"{directoryToTraverse}\"",
                    timeoutMs: 60_000, ct: ct);

                if (chownResult.ExitCode != 0)
                {
                    logger.LogWarning("chown exited with {ExitCode}: {StdErr}", chownResult.ExitCode, chownResult.StdErr);
                }
                else
                {
                    logger.LogInformation("Owner set successfully");
                }
            }
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

