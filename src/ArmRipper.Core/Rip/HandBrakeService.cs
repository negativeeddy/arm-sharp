using System.Text.RegularExpressions;
using ArmRipper.Core.Configuration;
using ArmRipper.Core.Infrastructure;
using ArmRipper.Core.Infrastructure.Data;
using ArmRipper.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArmRipper.Core.Rip;

public sealed partial class HandBrakeService(
    ICliProcessRunner runner,
    ILoggerFactory loggerFactory,
    ArmDbContext db,
    IOptions<ArmSettings> settings,
    ITranscodeSlotLimiter transcodeSlotLimiter) : IHandBrakeService
{
    private readonly ILogger logger = loggerFactory.CreateLogger("HandBrakeService");
    public async Task<CliResult> TranscodeMkvAsync(Job job, string rawPath, string outputPath, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        logger.LogInformation("Starting HandBrake for MKV files");

        if (!Directory.Exists(rawPath))
        {
            logger.LogWarning("Raw path does not exist: {RawPath}", rawPath);
            return new CliResult(-1, "", "Raw path not found", false);
        }

        CliResult? lastResult = null;
        var ext = settings.Value.DestExt ?? "mp4";

        var anySuccess = false;
        var mkvFiles = Directory.EnumerateFiles(rawPath, "*.mkv").ToList();
        var fileNum = 0;
        foreach (var file in mkvFiles)
        {
            fileNum++;
            job.ProgressMessage = $"Transcoding file {fileNum} of {mkvFiles.Count}";
            await db.SaveChangesAsync(ct);
            var destFile = Path.GetFileNameWithoutExtension(file);
            var outputFile = Path.Combine(outputPath, $"{destFile}.{ext}");

            logger.LogInformation("Transcoding {File} to {Output}", file, outputFile);
            var cmd = BuildCommand(file, outputFile, job, trackNumber: null, mainFeature: false);
            var effectiveMax = job.Config?.MaxConcurrentTranscodes ?? settings.Value.MaxConcurrentTranscodes;
            lastResult = await RunHandBrakeCommandAsync(cmd, effectiveMax, job, ct, progress);

            if (lastResult.ExitCode != 0)
            {
                var msg = $"HandBrake failed on {file} with code {lastResult.ExitCode}: {lastResult.StdErr}";
                logger.LogError(msg);
                var failTrack = job.Tracks.FirstOrDefault(t => t.FileName == $"{destFile}.mkv");
                if (failTrack is not null)
                {
                    failTrack.Status = "fail";
                    failTrack.Error = msg;
                    await db.SaveChangesAsync(ct);
                }
                continue;
            }

            var track = job.Tracks.FirstOrDefault(t => t.FileName == $"{destFile}.mkv");
            if (track is null)
            {
                track = new Track
                {
                    JobId = job.Id,
                    FileName = $"{destFile}.{ext}",
                    OrigFileName = $"{destFile}.mkv",
                    Source = "MakeMKV",
                    BaseName = job.Title,
                };
                db.Tracks.Add(track);
            }
            else
            {
                track.OrigFileName = track.FileName;
                track.FileName = $"{destFile}.{ext}";
            }
            track.Ripped = true;
            track.Status = "success";
            await db.SaveChangesAsync(ct);
            anySuccess = true;
        }

        if (!anySuccess)
        {
            job.Status = JobState.Failure;
            job.Errors = "All tracks failed to transcode";
            await db.SaveChangesAsync(ct);
            return lastResult ?? new CliResult(-1, "", "All tracks failed to transcode", true);
        }

        return lastResult ?? new CliResult(0, "", "", false);
    }

    public async Task<CliResult> TranscodeMainFeatureAsync(Job job, string rawPath, string outputPath, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        logger.LogInformation("Starting DVD/BluRay main feature transcoding");

        var ext = settings.Value.DestExt ?? "mp4";
        var filename = $"{job.Title}.{ext}";
        var outputFile = Path.Combine(outputPath, filename);

        await GetTrackInfoAsync(rawPath, job, ct);

        var track = job.Tracks.FirstOrDefault(t => t.MainFeature);
        if (track is null)
        {
            logger.LogWarning("No main feature found by HandBrake, falling back to all titles");
            return await TranscodeAllAsync(job, rawPath, outputPath, progress, ct);
        }

        track.FileName = track.OrigFileName = filename;
        await db.SaveChangesAsync(ct);

        job.ProgressMessage = "Transcoding main feature";
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Ripping main feature to {Output}", outputFile);
        var cmd = BuildCommand(rawPath, outputFile, job, trackNumber: null, mainFeature: true);
        var effectiveMax = job.Config?.MaxConcurrentTranscodes ?? settings.Value.MaxConcurrentTranscodes;

        try
        {
            var result = await RunHandBrakeCommandAsync(cmd, effectiveMax, job, ct, progress);
            if (result.ExitCode != 0)
            {
                var err = $"HandBrake main feature transcoding failed with code {result.ExitCode}: {result.StdErr}";
                logger.LogError(err);
                throw new InvalidOperationException(err);
            }

            if (!File.Exists(outputFile))
            {
                var err = $"HandBrake main feature did not produce output file: {outputFile}";
                logger.LogError(err);
                throw new InvalidOperationException(err);
            }

            logger.LogInformation("HandBrake call successful");
            track.Ripped = true;
            track.Status = "success";
            await db.SaveChangesAsync(ct);
            return result;
        }
        catch (Exception ex)
        {
            var err = $"HandBrake main feature transcoding failed: {ex.Message}";
            logger.LogError(err);
            track.Status = "fail";
            track.Error = err;
            job.Errors = err;
            job.Status = JobState.Failure;
            await db.SaveChangesAsync(ct);
            return new CliResult(-1, "", err, false);
        }
    }

    public async Task<CliResult> TranscodeAllAsync(Job job, string rawPath, string outputPath, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        logger.LogInformation("Starting BluRay/DVD transcoding - All titles");

        await GetTrackInfoAsync(rawPath, job, ct);

        CliResult? lastResult = null;
        var minLength = settings.Value.MinLength;
        var maxLength = settings.Value.MaxLength;
        var ext = settings.Value.DestExt ?? "mp4";

        var anySuccess = false;
        var eligibleTracks = job.Tracks
            .Where(t => t.TrackNumberInt.HasValue &&
                        t.TrackNumberInt.Value <= (job.NoOfTitles ?? 0) &&
                        (t.Length ?? 0) >= minLength &&
                        (t.Length ?? 0) <= maxLength)
            .Select(t => (Track: t, TrackNo: t.TrackNumberInt!.Value))
            .ToList();
        var processedCount = 0;
        foreach (var eligible in eligibleTracks)
        {
            var track = eligible.Track;
            var trackNo = eligible.TrackNo;

            processedCount++;
            job.ProgressMessage = $"Transcoding track {trackNo} ({processedCount} of {eligibleTracks.Count})";
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Processing track #{TrackNo} of {Total}", trackNo, job.NoOfTitles);

            track.FileName = track.OrigFileName = $"title_{trackNo}.{ext}";
            var outputFile = Path.Combine(outputPath, track.FileName);
            await db.SaveChangesAsync(ct);

            logger.LogInformation("Transcoding title {TrackNo} to {Output}", trackNo, outputFile);
            var cmd = BuildCommand(rawPath, outputFile, job, trackNo, mainFeature: false);
            var effectiveMax = job.Config?.MaxConcurrentTranscodes ?? settings.Value.MaxConcurrentTranscodes;

            try
            {
                lastResult = await RunHandBrakeCommandAsync(cmd, effectiveMax, job, ct, progress);
                if (lastResult.ExitCode != 0)
                {
                    var err = $"HandBrake encoding of title {trackNo} failed with code {lastResult.ExitCode}: {lastResult.StdErr}";
                    logger.LogError(err);
                    track.Status = "fail";
                    track.Error = err;
                    await db.SaveChangesAsync(ct);
                    continue;
                }

                if (!File.Exists(outputFile))
                {
                    var err = $"HandBrake did not produce output file for title {trackNo}: {outputFile}";
                    logger.LogError(err);
                    track.Status = "fail";
                    track.Error = err;
                    await db.SaveChangesAsync(ct);
                    continue;
                }

                track.Ripped = true;
                track.Status = "success";
                await db.SaveChangesAsync(ct);
                anySuccess = true;
            }
            catch (Exception ex)
            {
                var err = $"HandBrake encoding of title {trackNo} failed: {ex.Message}";
                logger.LogError(err);
                track.Status = "fail";
                track.Error = err;
                await db.SaveChangesAsync(ct);
            }
        }

        if (!anySuccess)
        {
            job.Status = JobState.Failure;
            job.Errors = "All tracks failed to transcode";
            await db.SaveChangesAsync(ct);
        }

        return lastResult ?? new CliResult(0, "", "", false);
    }

    private string BuildCommand(string inputPath, string outputPath, Job job, int? trackNumber, bool mainFeature)
    {
        // HandBrake CLI has no --gpu flag; use CUDA_VISIBLE_DEVICES env var
        // to target a specific GPU for NVENC/NVDEC.
        var gpuIndex = job.Config?.GpuIndex ?? settings.Value.GpuIndex;
        var gpuPrefix = gpuIndex.HasValue ? $"CUDA_VISIBLE_DEVICES={gpuIndex.Value} " : "";

        var cmd = $"{gpuPrefix}nice HandBrakeCLI -i \"{inputPath}\" -o \"{outputPath}\"";

        if (mainFeature)
            cmd += " --main-feature";

        var (hbPreset, hbArgs) = GetHbSettings(job);
        if (!string.IsNullOrEmpty(hbPreset))
            cmd += $" --preset \"{hbPreset}\"";

        if (trackNumber.HasValue)
            cmd += $" -t {trackNumber}";

        if (!string.IsNullOrEmpty(hbArgs))
            cmd += $" {hbArgs}";

        if (settings.Value.TestMode)
            cmd += " --start-at duration:0 --stop-at duration:30";

        cmd += " 2>&1";
        return cmd;
    }

    private (string? preset, string? args) GetHbSettings(Job job)
    {
        if (job.DiscType == DiscType.Dvd)
            return (job.Config?.HbPresetDvd ?? settings.Value.HbPresetDvd,
                    job.Config?.HbArgsDvd ?? settings.Value.HbArgsDvd);

        if (job.DiscType is DiscType.Bluray or DiscType.Uhd)
            return (job.Config?.HbPresetBd ?? settings.Value.HbPresetBd,
                    job.Config?.HbArgsBd ?? settings.Value.HbArgsBd);

        return (settings.Value.HbPresetDvd, settings.Value.HbArgsDvd);
    }

    private async Task<CliResult> RunHandBrakeCommandAsync(string cmd, int maxConcurrent, Job job, CancellationToken ct, IProgress<int>? progress = null)
    {
        await using var slot = await transcodeSlotLimiter.AcquireAsync(maxConcurrent, ct);

        // Slot acquired — update status from Waiting to Active
        if (job.Status == JobState.TranscodeWaiting)
        {
            job.Status = JobState.TranscodeActive;
            job.ProgressMessage = "Transcoding...";
            await db.SaveChangesAsync(ct);
        }

        logger.LogInformation("HandBrake command: {Command}", cmd);

        var lines = new List<string>();
        var stderrLines = new List<string>();
        var escaped = $"-c \"{cmd.Replace("\"", "\\\"")}\"";
        var exitCode = -1;

        await foreach (var (line, isErr, code) in runner.RunStreamingAllAsync("bash", escaped, ct: ct))
        {
            if (code.HasValue)
            {
                exitCode = code.Value;
                break;
            }

            // Log each line live so it appears in the job log with proper formatting
            if (isErr)
            {
                logger.LogWarning("HandBrake stderr: {Line}", line);
                stderrLines.Add(line!);
            }
            else
            {
                logger.LogInformation("HandBrake: {Line}", line);
                lines.Add(line!);
            }

            // Progress lines can come through either stdout or stderr depending on 2>&1
            ParseHandBrakeProgress(line!, progress);
        }

        var result = new CliResult(exitCode, string.Join("\n", lines), string.Join("\n", stderrLines), exitCode != 0);

        if (result.ExitCode != 0)
            logger.LogError("HandBrake failed with code {Code}: {Error}", result.ExitCode, result.StdErr);

        return result;
    }

    private static void ParseHandBrakeProgress(string line, IProgress<int>? progress)
    {
        if (progress is null) return;
        var match = Regex.Match(line, @"Encoding:\s*task\s+\d+\s+of\s+\d+,\s*([\d.]+)\s*%");
        if (match.Success && double.TryParse(match.Groups[1].Value, out var pct))
            progress.Report((int)pct);
    }

    private async Task GetTrackInfoAsync(string sourcePath, Job job, CancellationToken ct)
    {
        logger.LogInformation("Using HandBrake to get information on all tracks");

        var cmd = $"HandBrakeCLI -i \"{sourcePath}\" -t 0 --scan 2>&1";
        var result = await runner.RunAsync("bash", $"-c \"{cmd.Replace("\"", "\\\"")}\"", timeoutMs: 300_000, ct: ct);

        if (result.ExitCode != 0 && string.IsNullOrWhiteSpace(result.StdOut))
        {
            logger.LogInformation("HandBrake unable to get track information");
            return;
        }

        db.Tracks.RemoveRange(db.Tracks.Where(t => t.JobId == job.Id));
        await db.SaveChangesAsync(ct);

        var lines = result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var titlesPattern = TitleCountPattern();
        var durationPattern = DurationPattern();
        var titleStartPattern = TitleStartPattern();
        var fpsAspectPattern = FpsAspectPattern();
        var mainFeaturePattern = MainFeaturePattern();

        var seconds = 0;
        var tNo = 0;
        var fps = 0.0;
        var aspect = 0;
        var mainFeature = false;
        var titlesFound = false;

        foreach (var line in lines)
        {
            if (!titlesFound)
            {
                var m = titlesPattern.Match(line);
                if (m.Success)
                {
                    job.NoOfTitles = int.Parse(m.Groups[2].Value);
                    await db.SaveChangesAsync(ct);
                    titlesFound = true;
                }
            }

            var titleMatch = titleStartPattern.Match(line);
            if (titleMatch.Success)
            {
                if (tNo != 0)
                    AddTrack(job, tNo, seconds, aspect, fps, mainFeature, "HandBrake");

                mainFeature = false;
                if (int.TryParse(titleMatch.Groups[1].Value, out var parsedNo))
                    tNo = parsedNo;
            }

            var durMatch = durationPattern.Match(line);
            if (durMatch.Success)
            {
                var parts = durMatch.Groups[1].Value.Split(':');
                if (parts.Length == 3 &&
                    int.TryParse(parts[0], out var h) &&
                    int.TryParse(parts[1], out var m) &&
                    int.TryParse(parts[2], out var s))
                {
                    seconds = h * 3600 + m * 60 + s;
                }
            }

            if (mainFeaturePattern.IsMatch(line))
                mainFeature = true;

            var fpsMatch = fpsAspectPattern.Match(line);
            if (fpsMatch.Success)
            {
                aspect = int.TryParse(fpsMatch.Groups[1].Value.Replace(",", ""), out var a) ? a : 0;
                fps = double.TryParse(fpsMatch.Groups[2].Value, out var f) ? f : 0.0;
            }
        }

        if (tNo != 0)
            AddTrack(job, tNo, seconds, aspect, fps, mainFeature, "HandBrake");
    }

    private void AddTrack(Job job, int trackNo, int seconds, int aspect, double fps, bool mainFeature, string source)
    {
        var track = new Track
        {
            JobId = job.Id,
            TrackNumber = trackNo.ToString(),
            Length = seconds,
            AspectRatio = aspect > 0 ? aspect.ToString() : null,
            Fps = fps,
            MainFeature = mainFeature,
            Source = source,
            BaseName = job.Title,
            Ripped = seconds > (settings.Value.MinLength)
        };

        db.Tracks.Add(track);
    }

    [GeneratedRegex(@"scan: (BD|DVD) has (\d{1,3}) title\(s\)")]
    private static partial Regex TitleCountPattern();

    [GeneratedRegex(@".*\+ title (\d+)")]
    private static partial Regex TitleStartPattern();

    [GeneratedRegex(@".*duration:\s*(\d{2}:\d{2}:\d{2})")]
    private static partial Regex DurationPattern();

    [GeneratedRegex(@"Main Feature")]
    private static partial Regex MainFeaturePattern();

    [GeneratedRegex(@"(\d+\.\d+)\s+fps")]
    private static partial Regex FpsAspectPattern();

}
