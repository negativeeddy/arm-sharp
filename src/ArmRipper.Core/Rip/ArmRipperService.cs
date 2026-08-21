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
    IRipRedirectService ripRedirectService,
    IEpisodeIdentificationOrchestrator? episodeOrchestrator = null) : IArmRipperService
{
    private readonly ILogger logger = loggerFactory.CreateLogger("ArmRipperService");
    private static readonly TimeSpan ProgressBroadcastInterval = TimeSpan.FromMilliseconds(200);
    private readonly ConcurrentDictionary<string, (int Percent, DateTime LastBroadcastUtc)> progressBroadcastState = new();

    /// <summary>Per-job signals used to park the pipeline during Manual Selection
    /// wait without polling. Keyed by job ID.</summary>
    private static readonly ConcurrentDictionary<int, TaskCompletionSource<bool>> manualSelectionSignals = new();

    /// <summary>
    /// Signal the waiting pipeline to resume with the user's track selections.
    /// Called by the API endpoint when the user clicks "Continue Rip".
    /// Returns false if the job was not waiting.</summary>
    public static bool SignalManualSelection(int jobId)
    {
        if (manualSelectionSignals.TryRemove(jobId, out var tcs))
        {
            tcs.TrySetResult(true);
            return true;
        }
        return false;
    }

    /// <summary>Cancel a waiting manual selection (e.g. on job cancellation).</summary>
    public static void CancelManualSelection(int jobId)
    {
        if (manualSelectionSignals.TryRemove(jobId, out var tcs))
            tcs.TrySetCanceled();
    }

    /// <summary>Fraction of the longest duration that counts as a "near tie".</summary>
    private const double MainFeatureTieToleranceRatio = 0.03;

    /// <summary>Absolute floor (seconds) for the near-tie window, so short discs
    /// still get a meaningful window.</summary>
    private const double MainFeatureTieToleranceFloorSeconds = 120.0;

    /// <summary>
    /// Selects the track to treat as the main feature from a list whose Process
    /// flags are already populated. The longest eligible track wins, but when one
    /// or more tracks are within a small tolerance of the longest duration (e.g.
    /// a featurette that is marginally longer than the movie), the tie is broken
    /// by file size, then chapter count, then (optionally) widescreen, then
    /// duration. If no track is eligible, falls back to the longest track overall
    /// so a disc of only short clips still rips its longest title.
    /// </summary>
    internal static Track? SelectMainFeatureTrack(IReadOnlyList<Track> tracks, bool preferWidescreen)
    {
        var eligible = tracks.Where(t => t.Process).ToList();
        var pool = eligible.Count > 0 ? eligible : tracks.ToList();
        if (pool.Count == 0)
            return null;

        var maxDuration = pool.Max(t => t.Length ?? 0);
        if (maxDuration <= 0)
            return pool[0];

        var tolerance = Math.Max(
            maxDuration * MainFeatureTieToleranceRatio,
            MainFeatureTieToleranceFloorSeconds);

        IOrderedEnumerable<Track> ranked = pool
            .Where(t => (t.Length ?? 0) >= maxDuration - tolerance)
            .OrderByDescending(MainFeatureSizeOf)
            .ThenByDescending(t => t.Chapters ?? 0);

        if (preferWidescreen)
            ranked = ranked.ThenByDescending(t => t.AspectRatio?.Contains("16:9") == true);

        return ranked
            .ThenByDescending(t => t.Length ?? 0)
            .First();
    }

    /// <summary>Bytes to compare tracks by, falling back to an estimate from duration.</summary>
    private static long MainFeatureSizeOf(Track t) => t.FileSize ?? (long)(t.Length ?? 0) * 1024;

    /// <summary>
    /// Applies a manual main-feature override for the job (or one remembered for
    /// the disc fingerprint) on top of the automatic selection. No-op when no
    /// override exists.
    /// </summary>
    private async Task ApplyMainFeatureOverrideAsync(Job job, IReadOnlyList<Track> tracks, CancellationToken ct)
    {
        var target = await ResolveMainFeatureTrackAsync(job, tracks, ct);
        if (target is null)
            return;

        var previous = tracks.FirstOrDefault(t => t.MainFeature);
        foreach (var track in tracks)
            track.MainFeature = ReferenceEquals(track, target);

        if (previous is not null && !ReferenceEquals(previous, target))
        {
            logger.LogInformation(
                "Main feature overridden to track {Track} (was {OldTrack}) for job {JobId}",
                target.TrackNumber, previous.TrackNumber, job.Id);
        }
    }

    /// <summary>
    /// Resolves which track should be the main feature for the rip, consulting, in
    /// order: a manual per-job override (freshly read from the DB so a mid-rip
    /// redirect is honored), a per-fingerprint override, then the automatic
    /// selection marked on <see cref="Track.MainFeature"/>.
    /// </summary>
    private async Task<Track?> ResolveMainFeatureTrackAsync(Job job, IReadOnlyList<Track> tracks, CancellationToken ct)
    {
        var overrideNumber = job.MainFeatureOverrideTrackNumber
            ?? await db.Jobs.AsNoTracking()
                .Where(j => j.Id == job.Id)
                .Select(j => j.MainFeatureOverrideTrackNumber)
                .FirstOrDefaultAsync(ct);

        if (!string.IsNullOrEmpty(overrideNumber))
        {
            var manual = tracks.FirstOrDefault(t => t.TrackNumber == overrideNumber);
            if (manual is not null)
                return manual;
        }

        if (!string.IsNullOrEmpty(job.DiscFingerprint))
        {
            var fingerprintTrackNumber = await db.DiscMetadata.AsNoTracking()
                .Where(d => d.Fingerprint == job.DiscFingerprint)
                .Select(d => d.MainFeatureTrackNumber)
                .FirstOrDefaultAsync(ct);

            if (!string.IsNullOrEmpty(fingerprintTrackNumber))
            {
                var remembered = tracks.FirstOrDefault(t => t.TrackNumber == fingerprintTrackNumber);
                if (remembered is not null)
                    return remembered;
            }
        }

        return tracks.FirstOrDefault(t => t.MainFeature);
    }

    /// <summary>
    /// Deletes leftover partial MakeMKV output for the single track that was
    /// being ripped when a redirect cancelled it, so the re-rip starts from a
    /// clean output directory. Only the cancelled track's file is removed —
    /// completed files from other tracks (e.g. an earlier rip of the same
    /// title) are left untouched.
    /// </summary>
    private static void CleanupPartialRipOutput(string makeMkvOutPath, Track track)
    {
        if (!Directory.Exists(makeMkvOutPath))
            return;

        // The exact output file name MakeMKV reported for this track in the
        // info scan (TINFO Filename field), or MakeMKV's conventional
        // "&lt;title&gt;_t{index}.mkv" name where index is the 0-based title index
        // (TrackNumber - 1), zero-padded to two digits.
        var exactName = string.IsNullOrEmpty(track.FileName)
            ? null
            : Path.GetFileName(track.FileName);

        var suffix = int.TryParse(track.TrackNumber, out var trackNumber) && trackNumber > 0
            ? $"t{trackNumber - 1:D2}.mkv"
            : null;

        foreach (var file in Directory.EnumerateFiles(makeMkvOutPath, "*.mkv"))
        {
            var name = Path.GetFileName(file);
            var isTarget = name.Equals(exactName, StringComparison.OrdinalIgnoreCase)
                || (suffix is not null && name.EndsWith("_" + suffix, StringComparison.OrdinalIgnoreCase));
            if (!isTarget)
                continue;

            try
            {
                File.Delete(file);
            }
            catch (Exception)
            {
                // Best-effort — a file in use by the dying process may linger.
            }
        }
    }

    /// <summary>
    /// Carries mutable path state across the rip phases.  Used only as an
    /// internal shuttle between the orchestrator and its extracted sub-phases
    /// so that each phase can stay small and independently testable.
    /// </summary>
    internal sealed class RipContext
    {
        public required string JobTitle { get; init; }
        public required string TranscodeOutPath { get; set; }
        public required string FinalDirectory { get; set; }
        public required string FinalBasePath { get; init; }
        public required string MakeMkvOutPath { get; init; }
        public string? TranscodeInPath { get; set; }
        public required bool UseMakeMkv { get; init; }
    }

    public async Task<string> RipVisualMediaAsync(Job job, string logFile, bool hasDupes, bool protection, CancellationToken ct = default)
    {
        // Phase 1 – compute paths, set initial stage, apply dupe-folder suffix.
        var ctx = await ComputeRipContextAsync(job, hasDupes, protection, ct);

        // Phase 2 – MakeMKV rip (idempotent, already extracted).
        if (ctx.UseMakeMkv)
        {
            ctx.TranscodeInPath = await PrepareTranscodeInputPathAsync(job, ctx.JobTitle, ctx.MakeMkvOutPath, ct);
        }

        // Phase 2b – eject disc and reload job.
        await EjectAndReloadAsync(job, ct);

        // Phase 3 – optional test-mode trim.
        await TestModeTrimAsync(ctx.TranscodeInPath, ct);

        // Phase 4 – TV episode identification (after rip, before transcode).
        await IdentifyEpisodesAsync(job, ctx.MakeMkvOutPath, ct);

        // Phase 5 – transcode (idempotent).
        var transcodeSucceeded = await ExecuteTranscodeAsync(job, logFile, ctx, protection, ct);

        // Phase 6 – finalize: file moves, Emby scan, cleanup, notification.
        return await FinalizeAsync(job, ctx, hasDupes, transcodeSucceeded, ct);
    }

    // ── Phase 1: path computation and initial state ────────────────────────

    /// <summary>
    /// Computes all output paths, transitions the job to the Identify stage,
    /// and applies duplicate-folder suffixes when needed.
    /// </summary>
    internal async Task<RipContext> ComputeRipContextAsync(Job job, bool hasDupes, bool protection, CancellationToken ct)
    {
        var typeSubFolder = ConvertJobType(job.VideoType);
        var jobTitle = FixJobTitle(job);

        var transcodeOutPath = Path.Combine(job.Config?.TranscodePath ?? ArmPaths.GetTranscodePath(settings.Value), typeSubFolder, jobTitle);
        var finalDirectory = Path.Combine(job.Config?.CompletedPath ?? ArmPaths.GetCompletedPath(settings.Value), typeSubFolder, jobTitle);

        // Base output path before any duplicate-folder suffix is applied. The actual
        // directory is only needed once the transcode completes — if the title is
        // (re)identified after the rip has started, this is recomputed at finalize.
        var finalBasePath = finalDirectory;

        job.Stage ??= RipStage.Setup;
        job.TransitionToStage(RipStage.Identify);
        job.ProgressMessage ??= "Preparing to rip...";
        await db.SaveChangesAsync(ct);
        await BroadcastJobUpdateAsync(job);

        transcodeOutPath = CheckForDupeFolder(hasDupes, transcodeOutPath, job);
        finalDirectory = CheckForDupeFolder(hasDupes, finalDirectory, job);

        job.Path = finalDirectory;
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Processing files to: {TranscodeOutPath}", transcodeOutPath);

        var makeMkvOutPath = Path.Combine(job.Config?.RawPath ?? ArmPaths.GetRawPath(settings.Value), jobTitle);
        var useMakeMkv = RipWithMkv(job, protection);

        logger.LogDebug("Using MakeMKV: {UseMakeMkv}", useMakeMkv);

        return new RipContext
        {
            JobTitle = jobTitle,
            TranscodeOutPath = transcodeOutPath,
            FinalDirectory = finalDirectory,
            FinalBasePath = finalBasePath,
            MakeMkvOutPath = makeMkvOutPath,
            TranscodeInPath = job.DevPath,
            UseMakeMkv = useMakeMkv,
        };
    }

    // ── Phase 2b: eject disc and reload ────────────────────────────────────

    /// <summary>
    /// Ejects the disc after the rip completes (no-op when AutoEject is
    /// disabled), then reloads the job so mid-rip WebUI edits are picked up.
    /// </summary>
    private async Task EjectAndReloadAsync(Job job, CancellationToken ct)
    {
        await identifyService.EjectAsync(job, ct);
        job.Ejected = true;
        await db.SaveChangesAsync(ct);

        // Reload job from DB: user may have changed title/video-type via WebUI during the rip.
        await db.Entry(job).ReloadAsync(ct);
    }

    // ── Phase 3: test-mode trim ────────────────────────────────────────────

    /// <summary>
    /// When <see cref="ArmSettings.TestMode"/> is enabled, trims every MKV in
    /// the transcode input directory to 30 seconds for quick validation.
    /// </summary>
    internal async Task TestModeTrimAsync(string? transcodeInPath, CancellationToken ct)
    {
        if (!settings.Value.TestMode || transcodeInPath is null || !Directory.Exists(transcodeInPath))
            return;

        logger.LogInformation("Test mode: trimming raw MKV files to 30 seconds");
        // Respect the configured ffmpeg binary (same as FfmpegService) so a
        // custom path/wrapper is honored here too.
        var ffmpegCli = settings.Value.FfmpegCli;
        if (string.IsNullOrWhiteSpace(ffmpegCli))
            ffmpegCli = "ffmpeg";
        foreach (var file in Directory.EnumerateFiles(transcodeInPath, "*.mkv"))
        {
            var tmp = file + ".trimmed";
            var trimResult = await runner.RunAsync(ffmpegCli,
                $"-t 30 -i \"{file}\" -c copy -y \"{tmp}\"", timeoutMs: 60_000, ct: ct);
            if (trimResult.ExitCode == 0 && File.Exists(tmp))
            {
                File.Delete(file);
                File.Move(tmp, file);
            }
        }
    }

    // ── Phase 4: TV episode identification ─────────────────────────────────

    /// <summary>
    /// Runs the full provider chain (DiscDb → DvdCompare → FileBot → TMDB →
    /// TVDB → OMDB) to identify TV episode assignments for naming, so the
    /// transcode and move steps can use episode numbers/titles.
    /// </summary>
    private async Task IdentifyEpisodesAsync(Job job, string makeMkvOutPath, CancellationToken ct)
    {
        await db.Entry(job).Collection(j => j.Tracks).LoadAsync(ct);
        if (episodeOrchestrator is not null &&
            (job.VideoType is VideoContentType.Series or VideoContentType.Tv))
        {
            await RunEpisodeIdentificationAsync(job, makeMkvOutPath, ct);
        }
    }

    // ── Phase 5: transcode ─────────────────────────────────────────────────

    /// <summary>
    /// Runs the transcode (ffmpeg or HandBrake) if it has not already completed.
    /// Returns <c>true</c> when the transcode succeeded (or was already done).
    /// </summary>
    private async Task<bool> ExecuteTranscodeAsync(
        Job job, string logFile, RipContext ctx, bool protection, CancellationToken ct)
    {
        // Reload job from DB before transcode: user may have changed title/video-type
        // via WebUI during episode identification.
        await db.Entry(job).ReloadAsync(ct);

        // transcodeInPath must be set by this point — either from DevPath or MakeMKV output
        if (ctx.TranscodeInPath is null)
            throw new InvalidOperationException($"Job {job.Id}: transcodeInPath is null — DevPath may not have been set");

        var transcodeSucceeded = job.IsStageComplete(RipStage.Transcode);

        if (transcodeSucceeded)
        {
            logger.LogInformation("Stage 'transcode' already completed — skipping transcode");
        }
        else
        {
            await StartTranscodeAsync(job, logFile, ctx.TranscodeInPath, ctx.TranscodeOutPath, protection, ct);
            transcodeSucceeded = job.Status != JobState.Failure;
            if (transcodeSucceeded)
            {
                job.MarkStageComplete(RipStage.Transcode);
            }
            else
            {
                logger.LogWarning("Transcode phase failed — raw files will be retained for retry");
            }
            await db.SaveChangesAsync(ct);
            await BroadcastJobUpdateAsync(job);
        }

        return transcodeSucceeded;
    }

    // ── Phase 6: finalize ──────────────────────────────────────────────────

    /// <summary>
    /// Handles all post-transcode work: title recomputation, file moves, poster
    /// relocation, Emby library scan, permissions, raw-file cleanup, and final
    /// notification.
    /// </summary>
    private async Task<string> FinalizeAsync(
        Job job, RipContext ctx, bool hasDupes, bool transcodeSucceeded, CancellationToken ct)
    {
        job.TransitionToStage(RipStage.Finalize);
        job.ProgressMessage = "Finalizing...";
        await db.SaveChangesAsync(ct);
        await BroadcastJobUpdateAsync(job);

        // Refresh job from DB to pick up any title / year / video-type changes
        // the user made via the WebUI while the rip + transcode were running.
        await db.Entry(job).ReloadAsync(ct);

        // Handle skip-transcode path swap.
        logger.LogDebug("Transcode status: [{SkipTranscode}] and MakeMKV Status: [{UseMakeMkv}]",
            job.Config?.SkipTranscode ?? settings.Value.SkipTranscode, ctx.UseMakeMkv);

        if ((job.Config?.SkipTranscode ?? settings.Value.SkipTranscode) && ctx.UseMakeMkv)
        {
            DeleteRawFiles(new[] { ctx.TranscodeOutPath });
            ctx.TranscodeOutPath = ctx.TranscodeInPath!;
        }

        // Recompute the output path from the current (possibly newly-identified)
        // title/type. The output directory is only needed once the transcode is
        // complete, so if the title was identified or changed after the rip
        // started, relocate to the newly-computed location.
        logger.LogDebug("Job title manual status: [{TitleManual}]", job.TitleManual);

        var recomputedFinal = ComputeOutputPath(job, job.Config?.CompletedPath ?? ArmPaths.GetCompletedPath(settings.Value));
        if (!string.Equals(recomputedFinal, ctx.FinalBasePath, StringComparison.Ordinal))
        {
            logger.LogInformation("Output path changed to \"{Path}\" — relocating before finalize.", recomputedFinal);

            var staleFinalDirectory = ctx.FinalDirectory;

            // Re-apply dupe folder suffix — the recomputation above dropped it, and
            // CheckForDupeFolder decides whether one is needed (creating the directory
            // when the new location is fresh).
            ctx.FinalDirectory = CheckForDupeFolder(hasDupes, recomputedFinal, job);

            // Move the poster out of the stale location before removing it.
            RelocatePoster(job, ctx.FinalDirectory);

            DeleteRawFiles(new[] { staleFinalDirectory });
            job.Path = ctx.FinalDirectory;
            await db.SaveChangesAsync(ct);
        }

        await MoveFilesPostAsync(ctx.TranscodeOutPath!, job, ct);

        // Move the poster.png from the identification-time path to the correct
        // final directory (fixes orphaned posters left in "unidentified/").
        RelocatePoster(job, job.Path ?? ctx.FinalDirectory);

        await ScanEmbyAsync(job, ct);

        await SetPermissionsAsync(job.Path ?? ctx.FinalDirectory, job, ct);

        CleanupRawFiles(job, ctx, transcodeSucceeded);

        await NotifyExitAsync(job, ct);

        job.TransitionToStage(RipStage.Done);
        job.MarkStageComplete(RipStage.Finalize);
        job.ProgressMessage = null;
        await db.SaveChangesAsync(ct);
        await BroadcastJobUpdateAsync(job);

        logger.LogInformation("************* ARM processing complete *************");
        return job.Path ?? ctx.FinalDirectory;
    }

    /// <summary>
    /// Deletes raw source files when <see cref="ArmSettings.DelRawFiles"/> is
    /// enabled, but only if the transcode succeeded — otherwise the files are
    /// kept so the job can be retried.
    /// </summary>
    internal void CleanupRawFiles(Job job, RipContext ctx, bool transcodeSucceeded)
    {
        var delRaw = job.Config?.DelRawFiles ?? settings.Value.DelRawFiles;
        if (delRaw)
        {
            if (transcodeSucceeded)
            {
                DeleteRawFiles(new[] { ctx.TranscodeInPath, ctx.TranscodeOutPath, ctx.MakeMkvOutPath }.OfType<string>().ToArray());
            }
            else
            {
                logger.LogWarning("Transcode phase had errors — keeping raw files at {Paths} so the job can be retried",
                    string.Join(", ", new[] { ctx.TranscodeInPath, ctx.TranscodeOutPath, ctx.MakeMkvOutPath }.OfType<string>()));
            }
        }
        else
        {
            logger.LogInformation("DelRawFiles is disabled — keeping raw files at {Paths}",
                string.Join(", ", new[] { ctx.TranscodeInPath, ctx.TranscodeOutPath, ctx.MakeMkvOutPath }.OfType<string>()));
        }
    }

    private async Task<string?> PrepareTranscodeInputPathAsync(Job job, string jobTitle, string makeMkvOutPath, CancellationToken ct)
    {
        if (job.IsStageComplete(RipStage.Rip))
        {
            logger.LogInformation("Stage 'rip' already completed — skipping MakeMKV rip");
            logger.LogInformation("Using job.DevPath as transcode input: {DevPath}", job.DevPath);
            return job.DevPath;
        }

        if (settings.Value.TestMode)
        {
            logger.LogInformation("Test mode: ripping track 0 directly");

            if (!Directory.Exists(makeMkvOutPath))
                Directory.CreateDirectory(makeMkvOutPath);

            var mkvArgs = job.Config?.MkvArgs ?? settings.Value.MkvArgs ?? "";
            var testRipResult = await makeMkv.RipTrackAsync(job, "0", makeMkvOutPath, mkvArgs, 0, MkvProgress(job, "Ripping track 0", ct), ct);
            LogMakeMkvIssues(testRipResult, "test-mode rip");
            logger.LogInformation("Ripped track 0 in test mode");
            return makeMkvOutPath;
        }

        logger.LogInformation("************* Getting track info from MakeMKV *************");

        var config = job.Config;
        var minLengthCfg = config?.MinLength ?? settings.Value.MinLength;
        var maxLength = config?.MaxLength ?? settings.Value.MaxLength;

        // Use infoMinLength=0 when DiscDb is enabled so MakeMKV reports ALL tracks,
        // including short extras that may match DiscDb entries. The normal
        // minLengthCfg is only used for the rip phase, not the scan.
        var infoMinLength = settings.Value.DiscDbEnabled ? 0 : (int?)null;
        var tracks = await makeMkv.GetTrackInfoWithCacheAsync(job, jobTitle, infoMinLength, ct);

        // Encrypted BDs often return 0 tracks from info; rip all titles directly
        if (tracks.Count == 0 && job.DiscType is DiscType.Bluray or DiscType.Dvd or DiscType.Uhd)
        {
            job.TransitionToStage(RipStage.Identify);
            GuardStage(job, "identify", "Active/VideoInfo", () => job.Status is JobState.Active or JobState.VideoInfo);
            job.TransitionToStage(RipStage.Rip);
            job.Status = JobState.VideoRipping;
            job.ProgressMessage = "Starting rip...";
            await db.SaveChangesAsync(ct);
            await BroadcastJobUpdateAsync(job);

            // The info scan may have timed out with infoMinLength=0 on a
            // damaged disc. Before falling back to RipAllTitles, try a second
            // info scan with the normal configured minLength. If that succeeds,
            // the normal track selection (MainFeature, etc.) will be applied.
            // This prevents an identify-phase timeout from cascading into a rip
            // that bypasses track selection and rips everything.
            var retryTracks = await makeMkv.GetTrackInfoWithCacheAsync(job, jobTitle,
                infoMinLength: null, ct);

            if (retryTracks.Count > 0)
            {
                tracks = retryTracks;
                logger.LogInformation(
                    "0-track fallback: retry with normal minLength found {Count} tracks, " +
                    "proceeding with standard track selection", retryTracks.Count);
            }
            else
            {
                if (!Directory.Exists(makeMkvOutPath))
                    Directory.CreateDirectory(makeMkvOutPath);

                var mkvArgs = config?.MkvArgs ?? settings.Value.MkvArgs ?? "";
                var fallbackRipResult = await makeMkv.RipAllTitlesAsync(job, makeMkvOutPath, mkvArgs, minLengthCfg, MkvProgress(job, "Ripping all titles", ct), ct);
                LogMakeMkvIssues(fallbackRipResult, "0-track fallback rip");
                logger.LogInformation("Ripped all titles from disc (0-track fallback)");

                if (!Directory.EnumerateFileSystemEntries(makeMkvOutPath).Any())
                {
                    var msg = fallbackRipResult.HadSkippedTitles || fallbackRipResult.HadReadError
                        ? $"MakeMKV rip produced no output files (disc read or title skip errors reported: {DescribeMakeMkvIssues(fallbackRipResult)})"
                        : "MakeMKV rip produced no output files";
                    logger.LogError(msg);
                    throw new InvalidOperationException(msg);
                }

                job.MarkStageComplete(RipStage.Rip);
                await db.SaveChangesAsync(ct);
                await BroadcastJobUpdateAsync(job);

                if (job.Config?.NotifyRip ?? settings.Value.NotifyRip)
                {
                    await notifications.NotifyAsync(job, NotificationService.NotifyTitle,
                        $"{job.Title} rip complete. Starting transcode.", ct);
                }

                logger.LogInformation("************* Ripping with MakeMKV completed *************");
                return makeMkvOutPath;
            }
        }

        // ── Main feature selection ──
        // A track is eligible when its length is inside the configured window. The
        // longest eligible track is normally the feature, but discs often carry an
        // extra whose duration is within a hair of the movie (e.g. a featurette a
        // few seconds longer than the feature). Comparing raw duration then picks
        // the wrong side, so when several tracks are near the longest duration we
        // break the tie by file size, then chapter count, then (optionally)
        // widescreen, then duration.
        foreach (var track in tracks)
        {
            var length = track.Length ?? 0;
            track.Process = length >= minLengthCfg && length <= maxLength;
        }

        var preferWidescreen = config?.PreferWidescreen ?? settings.Value.PreferWidescreen;
        var mainFeatureTrack = SelectMainFeatureTrack(tracks, preferWidescreen);

        if (mainFeatureTrack is not null)
        {
            foreach (var track in tracks)
                track.MainFeature = ReferenceEquals(track, mainFeatureTrack);

            logger.LogInformation(
                "Main feature: track {Track} (length {Length}s, chapters {Chapters}, size {Size}, aspect {Aspect})",
                mainFeatureTrack.TrackNumber, mainFeatureTrack.Length ?? 0,
                mainFeatureTrack.Chapters ?? 0, mainFeatureTrack.FileSize ?? 0,
                mainFeatureTrack.AspectRatio ?? "(unknown)");
        }

        // A manual per-job override (set from the UI, including mid-rip redirects)
        // or a per-fingerprint override remembered from an earlier rip wins over
        // the automatic selection above.
        await ApplyMainFeatureOverrideAsync(job, tracks, ct);

        foreach (var track in tracks)
            db.Tracks.Add(track);

        // Publish the title count immediately after the scan so the UI
        // can show how many titles MakeMKV found on the disc.  Previously
        // this was only set during transcode (HandBrake/ffmpeg), leaving
        // the JobDetail page blank until transcode started.
        job.NoOfTitles = tracks.Count;
        await db.SaveChangesAsync(ct);
        await BroadcastJobUpdateAsync(job);

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

        // ── Manual Selection wait ──
        // When Manual Selection mode is enabled, pause after the title scan and
        // wait for the user to choose which tracks to rip. The UI shows the track
        // table with checkboxes; the user clicks "Continue" to submit selections.
        if (config?.ManualSelection == true)
        {
            logger.LogInformation(
                "Manual Selection mode: pausing for user to select tracks for job {JobId}", job.Id);

            job.Status = JobState.ManualSelectionStarted;
            job.ProgressMessage = "Waiting for manual track selection...";
            await db.SaveChangesAsync(ct);
            await BroadcastJobUpdateAsync(job);

            // Park the pipeline — no polling. The TCS is completed when the
            // API endpoint receives the user's selection.
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            manualSelectionSignals[job.Id] = tcs;

            // Respect cancellation token — link it so abort/cancel wakes us up.
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var cancellationRegistration = linkedCts.Token.Register(() =>
            {
                CancelManualSelection(job.Id);
                linkedCts.Cancel();
            });

            try
            {
                var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(Timeout.Infinite, linkedCts.Token));

                if (completedTask != tcs.Task || tcs.Task.IsCanceled)
                {
                    // Cancelled via token (job abort/shutdown)
                    logger.LogInformation("Job cancelled during manual selection wait");
                    return null;
                }

                logger.LogInformation("Manual selection resumed by user for job {JobId}", job.Id);
            }
            finally
            {
                await cancellationRegistration.DisposeAsync();
                manualSelectionSignals.TryRemove(job.Id, out _);
            }

            // Apply the user's track selections: parse the JSON array of track
            // numbers, set Process=true only for selected tracks, false for the rest.
            if (!string.IsNullOrEmpty(job.ManualSelectionTrackNumbers))
            {
                try
                {
                    var selectedNumbers = System.Text.Json.JsonSerializer.Deserialize<List<string>>(
                        job.ManualSelectionTrackNumbers) ?? [];
                    var selectedSet = new HashSet<string>(selectedNumbers, StringComparer.Ordinal);

                    foreach (var track in tracks)
                    {
                        track.Process = track.TrackNumber is not null
                            && selectedSet.Contains(track.TrackNumber);
                    }

                    // Also update the MainFeature flag to the first selected track
                    // so downstream stages (transcode) know which is primary.
                    var firstSelected = tracks.FirstOrDefault(t => t.Process);
                    foreach (var track in tracks)
                        track.MainFeature = ReferenceEquals(track, firstSelected);

                    // Persist the updated Process flags
                    foreach (var track in tracks)
                        db.Entry(track).Property(x => x.Process).IsModified = true;

                    logger.LogInformation(
                        "Manual Selection: user selected {Count} of {Total} tracks for job {JobId}",
                        selectedNumbers.Count, tracks.Count, job.Id);
                }
                catch (System.Text.Json.JsonException ex)
                {
                    logger.LogError(ex, "Failed to parse ManualSelectionTrackNumbers for job {JobId}", job.Id);
                    job.Status = JobState.Failure;
                    job.Errors = "Invalid track selection data";
                    await db.SaveChangesAsync(ct);
                    await BroadcastJobUpdateAsync(job);
                    return null;
                }
            }

            job.Status = JobState.VideoRipping;
            job.ProgressMessage = "Starting rip...";
            await db.SaveChangesAsync(ct);
            await BroadcastJobUpdateAsync(job);
        }

        logger.LogInformation("************* Ripping disc with MakeMKV *************");
        job.TransitionToStage(RipStage.Identify);
        GuardStage(job, "identify", "Active/VideoInfo", () => job.Status is JobState.Active or JobState.VideoInfo);
        job.TransitionToStage(RipStage.Rip);
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
            var ripResults = new List<MakeMkvRipResult>();

            if (settings.Value.TestMode)
            {
                var firstTrack = eligibleTracks.FirstOrDefault();
                if (firstTrack is not null)
                {
                    var trackNum = firstTrack.TrackNumber ?? throw new InvalidOperationException(
                        $"Track {firstTrack.Id} has no TrackNumber — cannot rip");
                    ripResults.Add(await makeMkv.RipTrackAsync(job, trackNum, makeMkvOutPath, mkvArgs, 0, MkvProgress(job, "Ripping track 0", ct), ct));
                }
                else
                    ripResults.Add(await makeMkv.RipTrackAsync(job, "0", makeMkvOutPath, mkvArgs, 0, MkvProgress(job, "Ripping track 0", ct), ct));
            }
            else if (config?.MainFeature ?? settings.Value.MainFeature)
            {
                // MainFeature mode: only rip the single longest track (or the track
                // chosen by a manual/fingerprint override). DiscDb metadata mapping
                // still runs (for poster, title, etc.) but promoted extras are NOT
                // ripped in this mode.
                try
                {
                    while (true)
                    {
                        var main = await ResolveMainFeatureTrackAsync(job, tracks, ct);
                        if (main is null)
                            break;

                        // Keep MainFeature flags in sync so downstream stages use the
                        // same track (a mid-rip redirect may have changed the target).
                        var currentMain = tracks.FirstOrDefault(t => t.MainFeature);
                        if (!ReferenceEquals(currentMain, main))
                        {
                            foreach (var track in tracks)
                                track.MainFeature = ReferenceEquals(track, main);
                            await db.SaveChangesAsync(ct);
                        }

                        // We already identified the exact track (the longest one), so pass
                        // minLength=0 to prevent MakeMKV from filtering it out with --minlength.
                        var trackNum = main.TrackNumber ?? throw new InvalidOperationException(
                            $"Main-feature track {main.Id} has no TrackNumber — cannot rip");

                        // Register a per-job cancellation token so the UI can request a
                        // mid-rip redirect; linked to the pipeline token so a normal stop
                        // still aborts the rip.
                        var ripCts = ripRedirectService.BeginRip(job.Id, ct);
                        try
                        {
                            ripResults.Add(await makeMkv.RipTrackAsync(
                                job, trackNum, makeMkvOutPath, mkvArgs, 0,
                                MkvProgress(job, $"Ripping main feature (track {trackNum})", ripCts.Token),
                                ripCts.Token));
                            ripCount = 1;
                            break;
                        }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested && ripRedirectService.WasRedirectRequested(job.Id))
                        {
                            ripRedirectService.AcknowledgeRedirect(job.Id);
                            logger.LogInformation(
                                "Main-feature rip redirected for job {JobId}; re-ripping the newly-selected track", job.Id);
                            CleanupPartialRipOutput(makeMkvOutPath, main);
                        }
                        finally
                        {
                            ripCts.Dispose();
                        }
                    }
                }
                finally
                {
                    ripRedirectService.EndRip(job.Id);
                }
            }
            else if (maxLength > 99998 && eligibleTracks.All(t => string.IsNullOrEmpty(t.EpisodeTitle)))
            {
                // Fast path: rip everything >= minLength in a single MakeMKV pass.
                // Only safe when NO tracks have been DiscDb-promoted (no EpisodeTitle),
                // otherwise individual iteration is needed to respect Process flags.
                ripResults.Add(await makeMkv.RipAllTitlesAsync(job, makeMkvOutPath, mkvArgs, minLengthCfg, MkvProgress(job, "Ripping all titles", ct), ct));
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
                    if (track.TrackNumber is null)
                    {
                        logger.LogWarning("Track {TrackId} has no TrackNumber — skipping", track.Id);
                        continue;
                    }
                    var trackMinLength = !string.IsNullOrEmpty(track.EpisodeTitle) ? 0 : minLengthCfg;
                    ripResults.Add(await makeMkv.RipTrackAsync(job, track.TrackNumber, makeMkvOutPath, mkvArgs, trackMinLength, MkvProgress(job, $"Ripping track {trackNum} of {eligibleTracks.Count}", ct), ct));
                    ripCount++;
                }
            }

            foreach (var result in ripResults)
                LogMakeMkvIssues(result, "rip");

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

            string? mainFeatureFailure = null;
            var undersizedWarnings = new List<string>();

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
                    // Capture the info-scan estimate BEFORE overwriting it with the
                    // actual file length so undersized/truncated rips can be detected.
                    var expectedSize = track.FileSize ?? 0;
                    var expectedDuration = track.Length is int l && l > 0 ? (double?)l : null;

                    track.FileName = fileName;
                    track.FileSize = fileInfo.Length;
                    track.Ripped = true;

                    // Probe the output with ffprobe — the authoritative post-rip
                    // check that depends only on the file MakeMKV actually wrote,
                    // never on MakeMKV's internal title numbering. Cheap (<1 s per
                    // file); a null duration means "cannot verify" and the size gate
                    // remains the only check for that file.
                    var actualDuration = await ffmpeg.ProbeDurationAsync(file, ct);

                    // Update track length with the actual probed duration. When
                    // MakeMKV's "fast path" rip (all titles with --minlength) saves
                    // files, it renumbers output sequentially (t00, t01, …) which
                    // can mismatch info-scan track numbers. The ffprobe result is
                    // authoritative and ensures episode identification uses the
                    // correct duration for provider matching.
                    if (actualDuration is not null && actualDuration > 0)
                        track.Length = (int)actualDuration.Value;

                    // DiscDb-promoted tracks were deliberately ripped with
                    // minLength=0 because they may legitimately be shorter than the
                    // configured floor — do not re-impose the floor on them.
                    var trackMinLength = string.IsNullOrEmpty(track.EpisodeTitle) ? minLengthCfg : 0;

                    var verdict = VerifyRipOutput(
                        track.MainFeature,
                        expectedSize, fileInfo.Length,
                        expectedDuration, actualDuration,
                        trackMinLength,
                        MinRipSizeRatio, MinDurationRatio);

                    if (verdict == RipVerificationVerdict.Fail)
                    {
                        mainFeatureFailure = BuildRipVerificationFailure(
                            track, expectedSize, fileInfo.Length, expectedDuration, actualDuration);
                    }
                    else if (verdict == RipVerificationVerdict.Warn)
                    {
                        undersizedWarnings.Add(
                            $"track {track.TrackNumber} produced {DescribeRipVerification(expectedSize, fileInfo.Length, expectedDuration, actualDuration)}");
                    }
                }
            }

            foreach (var warning in undersizedWarnings)
                logger.LogWarning("Rip produced undersized output: {Detail}", warning);

            if (mainFeatureFailure is not null)
            {
                logger.LogError(mainFeatureFailure);
                job.Status = JobState.Failure;
                job.Errors = mainFeatureFailure;
                await db.SaveChangesAsync(ct);
                throw new InvalidOperationException(mainFeatureFailure);
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
        job.TransitionToStage(RipStage.Transcode);
        job.Status = JobState.TranscodeWaiting;
        job.ProgressMessage = "Waiting for transcode slot...";
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
            else if ((job.VideoType is VideoContentType.Unknown or VideoContentType.Movie) && (job.Config?.MainFeature ?? settings.Value.MainFeature))
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
            else if ((job.VideoType is VideoContentType.Unknown or VideoContentType.Movie) && (job.Config?.MainFeature ?? settings.Value.MainFeature))
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
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to start broadcast for job {JobId}", job.Id);
            }
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
                DiscId                = discId,
                SeriesTitle           = CleanSeriesTitle(job.Title ?? job.Label ?? "Unknown"),
                Season                = season,
                Tracks                = trackContexts,
                DiscDbHint            = makeMkvOutPath,  // FileBot CLI uses this for raw file path
                DiscNumber            = job.DiscNumber ?? ParseDiscNumber(job.Label),
                StartingEpisodeNumber = job.StartingEpisodeNumber
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

    /// <summary>
    /// Relocates the poster.png that was saved during identification to the
    /// correct final directory. During identification the VideoType and Year
    /// may not yet be known, so the poster can end up in "unidentified/" or
    /// under a title without the year suffix. This method moves it to the
    /// correct location and cleans up the empty stale directory.
    /// </summary>
    private void RelocatePoster(Job job, string finalDirectory)
    {
        var posterSavedPath = job.PosterSavedPath;
        if (string.IsNullOrEmpty(posterSavedPath) || !File.Exists(posterSavedPath))
            return;

        var posterDst = Path.Combine(finalDirectory, "poster.png");

        // Already in the right place — nothing to do.
        if (string.Equals(posterSavedPath, posterDst, StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            Directory.CreateDirectory(finalDirectory);
            File.Move(posterSavedPath, posterDst);
            logger.LogInformation("Relocated poster from {Old} to {New}", posterSavedPath, posterDst);

            // Clean up the empty stale directory left behind.
            var staleDir = Path.GetDirectoryName(posterSavedPath);
            if (staleDir is not null && Directory.Exists(staleDir) &&
                !Directory.EnumerateFileSystemEntries(staleDir).Any())
            {
                Directory.Delete(staleDir);
                logger.LogInformation("Removed empty stale poster directory {Dir}", staleDir);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to relocate poster from {Old} to {New}", posterSavedPath, posterDst);
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
        if (job.VideoType is VideoContentType.Series or VideoContentType.Tv)
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
            if (track.FileName is null)
            {
                logger.LogWarning("Track {TrackId} has no FileName — skipping move", track.Id);
                continue;
            }

            if (tracks.Count == 1)
            {
                MoveFiles(transcodeOutPath, track.FileName, job, true, track);
            }
            else
            {
                if (track.Source == "MakeMKV" && job.VideoType == VideoContentType.Movie)
                {
                    SkipTranscodeMovie(Directory.GetFiles(transcodeOutPath).Select(Path.GetFileName).Cast<string>().ToList(), job, transcodeOutPath);
                    break;
                }
                MoveFiles(transcodeOutPath, track.FileName, job, track.MainFeature, track);
            }
        }

        // ── Update job.Path for TV series: files now live under
        //     {completed}/tv/Series Name/Season XX/ instead of the flat
        //     {completed}/tv/DISC_LABEL/ directory that was set at startup.
        //     The Conductor uses job.Path for the final output verification.
        if (job.VideoType is VideoContentType.Series or VideoContentType.Tv)
        {
            var cleanSeries = CleanSeriesTitle(job.Title ?? "Unknown Series");
            var completedBase = job.Config?.CompletedPath ?? ArmPaths.GetCompletedPath(settings.Value);
            job.Path = Path.Combine(completedBase, "tv", SanitizeFileName(cleanSeries));
            logger.LogInformation("Updated job path to series directory: {Path}", job.Path);
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

        if (job.VideoType != VideoContentType.Movie) return;

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
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to stat raw file {Path}", tempPath);
        }

        // Build a lookup from filename to Track so we can pass DiscDb metadata
        var trackByFileName = new Dictionary<string, Track>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in job.Tracks)
        {
            if (!string.IsNullOrEmpty(t.FileName))
                trackByFileName[t.FileName] = t;
        }

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
                               (job.VideoType is VideoContentType.Series or VideoContentType.Tv);

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

            // Jellyfin convention: SxxExx - Title.ext (series name is in the directory)
            var episodeFile = Path.Combine(seasonDir,
                $"S{season:D2}E{episode:D2}{episodeTitle}.{destExt}");

            EnsureDirectory(seasonDir);
            logger.LogInformation("Track is a TV episode. Moving '{Src}' to '{Dst}'",
                Path.Combine(basePath, filename), episodeFile);
            MoveFileMain(Path.Combine(basePath, filename), episodeFile, logger);
            return;
        }

        // ── Extras routing by content type (DiscDb-mapped) ──
        var contentType = track?.ContentType;
        if (!string.IsNullOrEmpty(contentType) &&
            contentType != "main" &&
            contentType != "unknown")
        {
            // contentType is non-null here because the if-condition guards against null/empty
            var extrasSubFolder = GetExtrasSubFolder(contentType, job);
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
            var extrasPath = job.VideoType != VideoContentType.Series && !string.IsNullOrEmpty(job.Config?.ExtrasSub)
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

        // Strip season/disc suffix — handles both spaced and compact formats,
        // with optional trailing country/region code:
        //   "MY_NAME_IS_EARL_S1_D1"              → "MY_NAME_IS_EARL"
        //   "MY_NAME_IS_EARL_SEASON1_DISC2"       → "MY_NAME_IS_EARL"
        //   "How I Met Your Mother S3D1"          → "How I Met Your Mother"
        //   "HOW_I_MET_YOUR_MOTHER_S3D1"          → "HOW_I_MET_YOUR_MOTHER"
        //   "HOW_I_MET_YOUR_MOTHER_S2_D1_US"      → "HOW_I_MET_YOUR_MOTHER"
        result = System.Text.RegularExpressions.Regex.Replace(
            result, @"[_\s][Ss](?:EASON)?\d+[_\s]?[Dd](?:ISC)?\d+(?:[_\s][A-Za-z]{2,4})?$", "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // Replace underscores with spaces
        result = result.Replace('_', ' ').Trim();

        // After underscore→space conversion, also strip trailing season/disc
        // suffixes (e.g. from labels where underscores were already spaces).
        result = System.Text.RegularExpressions.Regex.Replace(
            result, @"\s+[Ss](?:EASON)?\d+\s?[Dd](?:ISC)?\d+(?:\s[A-Za-z]{2,4})?$", "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

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
        string title;
        if (!string.IsNullOrEmpty(job.Year) && job.Year != "0000")
        {
            if (!string.IsNullOrEmpty(job.TitleManual))
                title = $"{job.TitleManual} ({job.Year})";
            else
                title = $"{job.Title} ({job.Year})";
        }
        else
        {
            title = job.TitleManual ?? job.Title ?? "unknown";
        }

        return SanitizeDirectoryName(title);
    }

    /// <summary>
    /// Converts a title into a filesystem-safe folder/file name. Path separators
    /// are replaced (not dropped) so a title like "Fahrenheit 9/11" yields a single
    /// directory instead of nested subdirectories; any remaining characters that
    /// are invalid in filenames are stripped. Falls back to "unknown" when nothing
    /// usable remains.
    /// </summary>
    internal static string SanitizeDirectoryName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "unknown";

        var replaced = name.Replace('/', '_').Replace('\\', '_');
        var sanitized = SanitizeFileName(replaced).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "unknown" : sanitized;
    }

    internal static string ConvertJobType(VideoContentType videoType)
    {
        return videoType switch
        {
            VideoContentType.Movie => "movies",
            VideoContentType.Series => "tv",
            _ => "unidentified"
        };
    }

    /// <summary>
    /// Computes the base output directory (before any duplicate-folder suffix is
    /// applied) for a job's completed media from its current title/type. The
    /// output path is not needed until the transcode is complete, so this is used
    /// both at finalize time and by the WebUI to refresh <see cref="Job.Path"/> as
    /// soon as the title is identified after the rip has started.
    /// </summary>
    public static string ComputeOutputPath(Job job, string? completedPath)
        => Path.Combine(completedPath ?? ArmPaths.DefaultCompletedPath, ConvertJobType(job.VideoType), FixJobTitle(job));

    /// <summary>
    /// Minimum ratio of actual rip output size to the MakeMKV info-scan estimate
    /// before a rip target is considered grossly undersized. MakeMKV's FileSize
    /// estimate (TINFO field 11) is close to the output size for a healthy rip;
    /// a target landing far below it (e.g. a 9s file where a ~2h feature was
    /// expected) means the intended title was skipped or the disc is damaged.
    /// </summary>
    internal const double MinRipSizeRatio = 0.30;

    /// <summary>
    /// Minimum ratio of the ffprobe output duration to the MakeMKV info-scan
    /// estimate before a rip target is considered truncated / the wrong title.
    /// MakeMKV's duration (TINFO field 9) reflects the disc's title length; a
    /// salvaged clip or a wrong title can land far below it even when the file
    /// size happens to coincide. The bound is generous because damaged discs
    /// legitimately trim cells (MSG:3037/3038) and PAL/NTSC conversion shifts
    /// durations by small amounts.
    /// </summary>
    internal const double MinDurationRatio = 0.50;

    internal static bool IsRipUndersized(long expectedSize, long actualSize, double minRatio)
        => expectedSize > 0 && actualSize < expectedSize * minRatio;

    /// <summary>True when the probed duration is a fraction of the expected duration below <paramref name="minRatio"/>.
    /// Cannot-validate cases (missing expected or actual) never count as truncated.</summary>
    internal static bool IsRipDurationTruncated(double? expectedDuration, double? actualDuration, double minRatio)
        => expectedDuration is > 0 && actualDuration is not null && actualDuration < expectedDuration * minRatio;

    /// <summary>True when the probed duration is below the configured minimum title length.
    /// A zero/negative minLength disables the floor; a null actual duration cannot be validated.</summary>
    internal static bool IsRipDurationBelowMinLength(double? actualDuration, int minLength)
        => minLength > 0 && actualDuration is not null && actualDuration < minLength;

    /// <summary>Verdict for a single rip target's output-file verification.</summary>
    internal enum RipVerificationVerdict
    {
        /// <summary>Output matches the expected size/duration — no action.</summary>
        Pass,
        /// <summary>Output is undersized/truncated but this is not the main feature — warn only, keep the job running.</summary>
        Warn,
        /// <summary>Main-feature output is undersized/truncated — the job must fail so raw files are retained for retry.</summary>
        Fail
    }

    /// <summary>
    /// Verifies a rip output file against the expected info-scan estimate.
    /// Combines the cheap size gate (B2) with the authoritative ffprobe duration
    /// check (B3): a target is suspicious when it is grossly undersized, far
    /// shorter than expected, or shorter than the configured minimum title
    /// length. Severity is Fail for the main feature (the whole job must fail)
    /// and Warn for extras.
    /// </summary>
    internal static RipVerificationVerdict VerifyRipOutput(
        bool isMainFeature,
        long expectedSize, long actualSize,
        double? expectedDuration, double? actualDuration,
        int minLength,
        double minSizeRatio, double minDurationRatio)
    {
        var suspicious = IsRipUndersized(expectedSize, actualSize, minSizeRatio)
            || IsRipDurationTruncated(expectedDuration, actualDuration, minDurationRatio)
            || IsRipDurationBelowMinLength(actualDuration, minLength);

        if (!suspicious)
            return RipVerificationVerdict.Pass;

        return isMainFeature ? RipVerificationVerdict.Fail : RipVerificationVerdict.Warn;
    }

    /// <summary>Builds the failure message for a failed main-feature verification.</summary>
    private static string BuildRipVerificationFailure(
        Track track, long expectedSize, long actualSize, double? expectedDuration, double? actualDuration)
        => $"Main feature rip verification failed — track {track.TrackNumber} produced " +
           $"{DescribeRipVerification(expectedSize, actualSize, expectedDuration, actualDuration)}. " +
           "The intended title was likely skipped (damaged disc / navigation error) and the saved file is wrong.";

    /// <summary>Describes a rip output discrepancy in human-readable terms (size and/or duration).</summary>
    private static string DescribeRipVerification(long expectedSize, long actualSize, double? expectedDuration, double? actualDuration)
    {
        var parts = new List<string>();

        if (expectedSize > 0)
            parts.Add($"{actualSize:N0} bytes vs expected {expectedSize:N0} ({actualSize * 100.0 / expectedSize:F1}%)");

        if (expectedDuration is > 0 && actualDuration is not null)
            parts.Add($"{FormatDuration(actualDuration.Value)} vs expected {FormatDuration(expectedDuration.Value)}");

        return parts.Count > 0 ? string.Join("; ", parts) : $"{actualSize:N0} bytes vs no expected size";
    }

    private static string FormatDuration(double seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    /// <summary>
    /// Logs warnings for notable MakeMKV rip-phase messages (read errors, corrupt
    /// source sectors, skipped titles) that MakeMKV otherwise swallows.
    /// </summary>
    private void LogMakeMkvIssues(MakeMkvRipResult result, string context)
    {
        if (result is null) return;

        if (result.HadReadError)
            logger.LogWarning("MakeMKV reported read errors during {Context}", context);

        if (result.HadCorruptSource)
            logger.LogWarning("MakeMKV reported corrupt source sectors during {Context}", context);

        if (result.HadSkippedTitles)
            logger.LogWarning("MakeMKV skipped {Count} title(s) during {Context}: {Titles}",
                result.SkippedTitles.Count, context, DescribeMakeMkvIssues(result));
    }

    private static string DescribeMakeMkvIssues(MakeMkvRipResult result)
    {
        var parts = new List<string>();

        if (result.HadReadError)
            parts.Add("read errors");
        if (result.HadCorruptSource)
            parts.Add("corrupt source");
        if (result.HadSkippedTitles)
            parts.Add($"skipped: {string.Join("; ", result.SkippedTitles)}");

        return parts.Count > 0 ? string.Join(", ", parts) : "none";
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

    private string FindLargestFile(List<string> files, string mkvOutPath)
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
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to stat rip output file {Path}", fullPath);
            }
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

