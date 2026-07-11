using System.Text.Json;
using System.Text.RegularExpressions;
using ArmRipper.Core.Configuration;
using ArmRipper.Core.Infrastructure;
using ArmRipper.Core.Infrastructure.Data;
using ArmRipper.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArmRipper.Core.Rip;

public sealed partial class FfmpegService(
    ICliProcessRunner runner,
    ILoggerFactory loggerFactory,
    ArmDbContext db,
    IOptions<ArmSettings> settings,
    ITranscodeSlotLimiter transcodeSlotLimiter) : IFfmpegService
{
    private readonly ILogger logger = loggerFactory.CreateLogger("FfmpegService");

    public async Task<string> GetVersionAsync(CancellationToken ct = default)
    {
        var ffmpegCli = settings.Value.FfmpegCli;
        if (string.IsNullOrWhiteSpace(ffmpegCli))
            ffmpegCli = "ffmpeg";

        var result = await runner.RunAsync(ffmpegCli, "-version", timeoutMs: 10_000, ct: ct);
        var firstLine = result.StdOut
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));

        if (!string.IsNullOrWhiteSpace(firstLine))
            return firstLine;

        firstLine = result.StdErr
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));

        return string.IsNullOrWhiteSpace(firstLine) ? "Unknown" : firstLine;
    }

    public async Task<CliResult> TranscodeMkvAsync(Job job, string rawPath, string outputPath, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        logger.LogInformation("Starting FFmpeg for MKV files");

        var allStdOut = new List<string>();
        var allStdErr = new List<string>();

        if (!Directory.Exists(rawPath))
        {
            logger.LogWarning("Raw path does not exist: {RawPath}", rawPath);
            return new CliResult(-1, "", "Raw path not found", false);
        }

        EnsureDirectory(outputPath);

        var mkvFiles = Directory.EnumerateFiles(rawPath, "*.mkv").ToList();
        var fileNum = 0;
        foreach (var file in mkvFiles)
        {
            fileNum++;
            job.ProgressMessage = $"Transcoding file {fileNum} of {mkvFiles.Count}";
            await db.SaveChangesAsync(ct);
            var destFile = Path.GetFileNameWithoutExtension(file);
            var ext = settings.Value.DestExt ?? "mp4";
            var outFile = Path.Combine(outputPath, $"{destFile}.{ext}");

            logger.LogInformation("Transcoding {File} to {Output}", file, outFile);

            var track = job.Tracks.FirstOrDefault(t => t.FileName == $"{destFile}.mkv");
            if (track is not null)
            {
                track.OrigFileName = track.FileName;
                track.FileName = $"{destFile}.{ext}";
                await db.SaveChangesAsync(ct);
            }

            try
            {
                var totalSec = track?.Length is int l && l > 0 ? l : (int?)null;
                await RunTranscodeAsync(file, outFile, job, totalSec, allStdOut, allStdErr, progress, ct);
                logger.LogInformation("FFmpeg call successful");
                track ??= new Track
                {
                    JobId = job.Id,
                    FileName = $"{destFile}.{ext}",
                    OrigFileName = $"{destFile}.mkv",
                    Source = "MakeMKV",
                    BaseName = job.Title,
                };
                track.Ripped = true;
                track.Status = "success";
                if (track.Id == 0)
                    db.Tracks.Add(track);
                await db.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "FFmpeg encoding of {File} failed", file);
                if (track is not null)
                {
                    track.Status = "fail";
                    track.Error = ex.Message;
                    await db.SaveChangesAsync(ct);
                }
                // Continue to next file — don't abandon remaining tracks
                var existingErrors = job.Errors ?? "";
                job.Errors = string.IsNullOrEmpty(existingErrors)
                    ? $"{Path.GetFileName(file)}: {ex.Message}"
                    : $"{existingErrors}; {Path.GetFileName(file)}: {ex.Message}";
                await db.SaveChangesAsync(ct);
            }
        }

        // Only mark failure if ALL tracks failed
        if (mkvFiles.Count > 0 && !job.Tracks.Any(t => t.Ripped))
        {
            job.Status = JobState.Failure;
            job.Errors ??= "All MKV files failed to transcode";
            await db.SaveChangesAsync(ct);
        }

        return new CliResult(0, string.Join("\n", allStdOut), string.Join("\n", allStdErr), false);
    }

    public async Task<CliResult> TranscodeMainFeatureAsync(Job job, string rawPath, string outputPath, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        logger.LogInformation("Starting DVD/BluRay main feature transcoding");

        var allStdOut = new List<string>();
        var allStdErr = new List<string>();

        EnsureDirectory(outputPath);

        var ext = settings.Value.DestExt ?? "mp4";
        var filename = $"{job.Title}.{ext}";
        var outputFile = Path.Combine(outputPath, filename);

        await GetTrackInfoAsync(rawPath, job, ct);

        var track = job.Tracks.FirstOrDefault(t => t.MainFeature);
        if (track is null)
        {
            logger.LogWarning("No main feature found by FFmpeg, falling back to all titles");
            return await TranscodeAllAsync(job, rawPath, outputPath, progress, ct);
        }

        track.FileName = track.OrigFileName = filename;
        await db.SaveChangesAsync(ct);

        job.ProgressMessage = "Transcoding main feature";
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Ripping main feature to {Output}", outputFile);

        try
        {
            EnsureDirectory(outputPath);
            var totalSec = track.Length is int l && l > 0 ? l : (int?)null;
            await RunTranscodeAsync(rawPath, outputFile, job, totalSec, allStdOut, allStdErr, progress, ct);
            logger.LogInformation("FFmpeg call successful");
            track.Ripped = true;
            track.Status = "success";
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            var err = $"Call to FFmpeg failed: {ex.Message}";
            logger.LogError(err);
            track.Status = "fail";
            track.Error = job.Errors = err;
            job.Status = JobState.Failure;
            await db.SaveChangesAsync(ct);
            throw;
        }

        return new CliResult(0, string.Join("\n", allStdOut), string.Join("\n", allStdErr), false);
    }

    public async Task<CliResult> TranscodeAllAsync(Job job, string rawPath, string outputPath, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        logger.LogInformation("Starting BluRay/DVD transcoding - All titles");

        var allStdOut = new List<string>();
        var allStdErr = new List<string>();

        await GetTrackInfoAsync(rawPath, job, ct);

        EnsureDirectory(outputPath);
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

            var outFileName = $"title_{trackNo}.{ext}";
            var outFilePath = Path.Combine(outputPath, outFileName);

            track.FileName = track.OrigFileName = outFileName;
            await db.SaveChangesAsync(ct);

            try
            {
                var totalSec = track.Length is int l && l > 0 ? l : (int?)null;
                await RunTranscodeAsync(rawPath, outFilePath, job, totalSec, allStdOut, allStdErr, progress, ct);
                track.Status = "success";
                track.Ripped = true;
                await db.SaveChangesAsync(ct);
                anySuccess = true;
            }
            catch (Exception ex)
            {
                var err = $"FFmpeg encoding of title {trackNo} failed: {ex.Message}";
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

        return new CliResult(0, string.Join("\n", allStdOut), string.Join("\n", allStdErr), false);
    }

    private async Task RunTranscodeAsync(string inputFile, string outputFile, Job job, int? totalSeconds, List<string> stdOut, List<string> stdErr, IProgress<int>? progress, CancellationToken ct)
    {
        await using var slot = await transcodeSlotLimiter.AcquireAsync(ct);

        var (ffPreArgs, ffPostArgs) = GetFfSettings(job);

        var cmd = $"ffmpeg {ffPreArgs} -i \"{inputFile}\" {ffPostArgs} \"{outputFile}\"";
        logger.LogDebug("FFmpeg command: {Command}", cmd);

        var exitCode = -1;
        await foreach (var (line, isStdErr, code) in runner.RunStreamingAllAsync("bash", $"-c \"{cmd.Replace("\"", "\\\"")}\"", ct: ct))
        {
            if (code.HasValue)
            {
                exitCode = code.Value;
                break;
            }

            if (isStdErr)
            {
                stdErr.Add(line!);
                if (progress is not null && totalSeconds.HasValue)
                {
                    var pct = ParseFfProgress(line!, totalSeconds.Value);
                    if (pct.HasValue)
                        progress.Report(pct.Value);
                }
            }
            else
            {
                stdOut.Add(line!);
            }
        }

        if (exitCode != 0)
        {
            var err = $"FFmpeg exited with code {exitCode}";
            logger.LogError(err);
            throw new InvalidOperationException(err);
        }

        if (!File.Exists(outputFile))
        {
            var err = $"FFmpeg did not produce output file: {outputFile}";
            logger.LogError(err);
            throw new InvalidOperationException(err);
        }
    }

    private (string preArgs, string postArgs) GetFfSettings(Job job)
    {
        var config = job.Config;
        if (config is not null)
        {
            return (config.FfmpegPreFileArgs ?? "", config.FfmpegPostFileArgs ?? "");
        }

        return ("", "");
    }

    [GeneratedRegex(@"time=(\d{2}):(\d{2}):(\d{2})\.(\d{2})")]
    private static partial Regex FfTimeRegex();

    private static int? ParseFfProgress(string line, int totalSeconds)
    {
        var match = FfTimeRegex().Match(line);
        if (!match.Success) return null;

        var h = int.Parse(match.Groups[1].ValueSpan);
        var m = int.Parse(match.Groups[2].ValueSpan);
        var s = int.Parse(match.Groups[3].ValueSpan);
        var elapsed = h * 3600 + m * 60 + s;
        if (totalSeconds <= 0) return null;

        return (int)Math.Clamp(elapsed * 100.0 / totalSeconds, 0, 100);
    }

    private async Task GetTrackInfoAsync(string sourcePath, Job job, CancellationToken ct)
    {
        logger.LogInformation("Using ffprobe to get information on tracks");

        var result = await runner.RunAsync("ffprobe",
            $"-v error -print_format json -show_format -show_streams \"{sourcePath}\"",
            timeoutMs: 120_000, ct: ct);

        db.Tracks.RemoveRange(db.Tracks.Where(t => t.JobId == job.Id));
        await db.SaveChangesAsync(ct);

        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StdOut))
        {
            logger.LogInformation("ffprobe returned no data; registering fallback track");
            AddTrack(job, 1, 0, 0, 0.0, false, "FFmpeg");
            return;
        }

        var tracks = ParseProbeOutput(result.StdOut);
        if (tracks.Count == 0)
        {
            logger.LogInformation("No tracks parsed from ffprobe; registering fallback track");
            AddTrack(job, 0, 0, 0, 0.0, false, "FFmpeg");
            return;
        }

        EvaluateAndRegisterTracks(job, tracks);
    }

    private List<ProbeTrack> ParseProbeOutput(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var streams = root.GetProperty("streams").EnumerateArray()
            .Where(s => s.GetProperty("codec_type").GetString() == "video")
            .ToList();

        var formatDuration = 0;
        if (root.TryGetProperty("format", out var format))
        {
            if (format.TryGetProperty("duration", out var dur) && dur.ValueKind == JsonValueKind.String)
                int.TryParse(dur.GetString()?.Split('.')[0], out formatDuration);
        }

        if (streams.Count == 0)
        {
            return
            [
                new ProbeTrack { Title = 1, Duration = formatDuration, Fps = 0.0, Aspect = 0 }
            ];
        }

        var index = 1;
        var trackList = new List<ProbeTrack>();
        foreach (var stream in streams)
        {
            var dur = formatDuration;
            if (stream.TryGetProperty("duration", out var sd) && sd.ValueKind == JsonValueKind.String)
            {
                var s = sd.GetString()?.Split('.')[0];
                if (s is not null) int.TryParse(s, out dur);
            }

            var fps = 0.0;
            var fpsRaw = stream.TryGetProperty("r_frame_rate", out var rfr) ? rfr.GetString()
                : stream.TryGetProperty("avg_frame_rate", out var afr) ? afr.GetString()
                : null;

            if (fpsRaw is not null)
                fps = ParseFps(fpsRaw);

            var width = stream.TryGetProperty("width", out var w) ? w.GetInt32() : 0;
            var height = stream.TryGetProperty("height", out var h) ? h.GetInt32() : 0;
            var aspect = width > 0 && height > 0 ? Math.Round((double)width / height, 2) : 0;

            trackList.Add(new ProbeTrack
            {
                Title = index++,
                Duration = dur,
                Fps = fps,
                Aspect = aspect
            });
        }

        return trackList;
    }

    private static double ParseFps(string fpsRaw)
    {
        if (string.IsNullOrEmpty(fpsRaw) || fpsRaw == "0/0")
            return 0.0;

        try
        {
            if (fpsRaw.Contains('/'))
            {
                var parts = fpsRaw.Split('/');
                return double.Parse(parts[0]) / double.Parse(parts[1]);
            }

            return double.Parse(fpsRaw);
        }
        catch
        {
            return 0.0;
        }
    }

    private void EvaluateAndRegisterTracks(Job job, List<ProbeTrack> tracks)
    {
        var maxDur = -1;
        var mainTitle = 0;

        foreach (var t in tracks)
        {
            if (t.Duration > maxDur)
            {
                maxDur = t.Duration;
                mainTitle = t.Title;
            }
        }

        job.NoOfTitles = tracks.Count;

        foreach (var t in tracks)
        {
            AddTrack(job, t.Title, t.Duration, t.Aspect, t.Fps, t.Title == mainTitle, "FFmpeg");
        }
    }

    private void AddTrack(Job job, int trackNo, int duration, double aspect, double fps, bool mainFeature, string source)
    {
        var track = new Track
        {
            JobId = job.Id,
            TrackNumber = trackNo.ToString(),
            Length = duration,
            AspectRatio = aspect > 0 ? aspect.ToString("F2") : null,
            Fps = fps,
            MainFeature = mainFeature,
            Source = source,
            BaseName = job.Title,
            Ripped = duration > (settings.Value.MinLength)
        };

        db.Tracks.Add(track);
    }

    private static void EnsureDirectory(string path)
    {
        if (!Directory.Exists(path))
            Directory.CreateDirectory(path);
    }

    private record ProbeTrack
    {
        public int Title { get; init; }
        public int Duration { get; init; }
        public double Fps { get; init; }
        public double Aspect { get; init; }
    }
}
