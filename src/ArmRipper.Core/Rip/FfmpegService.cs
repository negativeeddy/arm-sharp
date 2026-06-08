using System.Diagnostics;
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
    ILogger<FfmpegService> logger,
    ArmDbContext db,
    IOptions<ArmSettings> settings) : IFfmpegService
{
    public async Task TranscodeMkvAsync(Job job, string rawPath, string outputPath, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        await SleepCheckAsync(ct);
        logger.LogInformation("Starting FFmpeg for MKV files");

        if (!Directory.Exists(rawPath))
        {
            logger.LogWarning("Raw path does not exist: {RawPath}", rawPath);
            return;
        }

        EnsureDirectory(outputPath);

        foreach (var file in Directory.EnumerateFiles(rawPath, "*.mkv"))
        {
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
                await RunTranscodeAsync(file, outFile, job, ct);
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
                job.Errors = ex.Message;
                job.Status = JobState.Failure;
                await db.SaveChangesAsync(ct);
                throw;
            }
        }
    }

    public async Task TranscodeMainFeatureAsync(Job job, string rawPath, string outputPath, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        await SleepCheckAsync(ct);
        logger.LogInformation("Starting DVD/BluRay main feature transcoding");

        EnsureDirectory(outputPath);

        var ext = settings.Value.DestExt ?? "mp4";
        var filename = $"{job.Title}.{ext}";
        var outputFile = Path.Combine(outputPath, filename);

        await GetTrackInfoAsync(rawPath, job, ct);

        var track = job.Tracks.FirstOrDefault(t => t.MainFeature);
        if (track is null)
        {
            var msg = "No main feature found by FFmpeg";
            logger.LogError(msg);
            throw new InvalidOperationException(msg);
        }

        track.FileName = track.OrigFileName = filename;
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Ripping main feature to {Output}", outputFile);

        try
        {
            EnsureDirectory(outputPath);
            await RunTranscodeAsync(rawPath, outputFile, job, ct);
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
    }

    public async Task TranscodeAllAsync(Job job, string rawPath, string outputPath, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        await SleepCheckAsync(ct);
        logger.LogInformation("Starting BluRay/DVD transcoding - All titles");

        await GetTrackInfoAsync(rawPath, job, ct);

        EnsureDirectory(outputPath);
        var minLength = settings.Value.MinLength;
        var maxLength = settings.Value.MaxLength;
        var ext = settings.Value.DestExt ?? "mp4";

        foreach (var track in job.Tracks)
        {
            if (!int.TryParse(track.TrackNumber, out var trackNo))
                continue;

            if (trackNo > (job.NoOfTitles ?? 0))
                continue;

            if ((track.Length ?? 0) < minLength)
            {
                logger.LogInformation("Track #{TrackNo}: length {Length}s < min {MinLength}s, skipping", trackNo, track.Length, minLength);
                continue;
            }

            if ((track.Length ?? 0) > maxLength)
            {
                logger.LogInformation("Track #{TrackNo}: length {Length}s > max {MaxLength}s, skipping", trackNo, track.Length, maxLength);
                continue;
            }

            logger.LogInformation("Processing track #{TrackNo} of {Total}", trackNo, job.NoOfTitles);

            var outFileName = $"title_{trackNo}.{ext}";
            var outFilePath = Path.Combine(outputPath, outFileName);

            track.FileName = track.OrigFileName = outFileName;
            await db.SaveChangesAsync(ct);

            try
            {
                await RunTranscodeAsync(rawPath, outFilePath, job, ct);
                track.Status = "success";
            }
            catch (Exception ex)
            {
                var err = $"FFmpeg encoding of title {trackNo} failed: {ex.Message}";
                logger.LogError(err);
                track.Status = "fail";
                track.Error = err;
                await db.SaveChangesAsync(ct);
                throw;
            }

            track.Ripped = true;
            await db.SaveChangesAsync(ct);
        }
    }

    private async Task SleepCheckAsync(CancellationToken ct)
    {
        var maxTranscodes = settings.Value.MaxConcurrentTranscodes;
        if (maxTranscodes <= 0)
            return;

        logger.LogInformation("Starting sleep check of ffmpeg");
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var count = Process.GetProcessesByName("ffmpeg").Length;
            if (count < maxTranscodes)
                break;

            logger.LogDebug("{Count} processes running. Sleeping 20s.", count);
            await Task.Delay(TimeSpan.FromSeconds(20), ct);
        }

        logger.LogInformation("Exiting sleep check of ffmpeg");
    }

    private async Task RunTranscodeAsync(string inputFile, string outputFile, Job job, CancellationToken ct)
    {
        var (ffPreArgs, ffPostArgs) = GetFfSettings(job);

        var cmd = $"ffmpeg {ffPreArgs} -i \"{inputFile}\" {ffPostArgs} \"{outputFile}\"";
        logger.LogDebug("FFmpeg command: {Command}", cmd);

        var result = await runner.RunAsync("bash", $"-c \"{cmd.Replace("\"", "\\\"")}\"", timeoutMs: 7200_000, ct: ct);

        if (result.ExitCode != 0)
            throw new InvalidOperationException($"FFmpeg failed with code {result.ExitCode}: {result.StdErr}");
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
