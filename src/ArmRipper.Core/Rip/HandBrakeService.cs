using System.Diagnostics;
using System.Runtime.CompilerServices;
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
    ILogger<HandBrakeService> logger,
    ArmDbContext db,
    IOptions<ArmSettings> settings) : IHandBrakeService
{
    public async Task<CliResult> TranscodeMkvAsync(Job job, string rawPath, string outputPath, CancellationToken ct)
    {
        await SleepCheckAsync(ct);
        logger.LogInformation("Starting HandBrake for MKV files");

        if (!Directory.Exists(rawPath))
        {
            logger.LogWarning("Raw path does not exist: {RawPath}", rawPath);
            return new CliResult(-1, "", "Raw path not found", false);
        }

        CliResult? lastResult = null;

        foreach (var file in Directory.EnumerateFiles(rawPath, "*.mkv"))
        {
            var destFile = Path.GetFileNameWithoutExtension(file);
            var ext = settings.Value.DestExt ?? "mp4";
            var outputFile = Path.Combine(outputPath, $"{destFile}.{ext}");

            logger.LogInformation("Transcoding {File} to {Output}", file, outputFile);
            var cmd = BuildCommand(file, outputFile, job, trackNumber: null, mainFeature: false);
            lastResult = await RunHandBrakeCommandAsync(cmd, ct);

            if (lastResult.ExitCode != 0)
            {
                var msg = $"HandBrake failed on {file} with code {lastResult.ExitCode}: {lastResult.StdErr}";
                logger.LogError(msg);
                throw new InvalidOperationException(msg);
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
            await db.SaveChangesAsync(ct);
        }

        return lastResult ?? new CliResult(0, "", "", false);
    }

    public async Task<CliResult> TranscodeMainFeatureAsync(Job job, string rawPath, string outputPath, CancellationToken ct)
    {
        await SleepCheckAsync(ct);
        logger.LogInformation("Starting DVD/BluRay main feature transcoding");

        var ext = settings.Value.DestExt ?? "mp4";
        var filename = $"{job.Title}.{ext}";
        var outputFile = Path.Combine(outputPath, filename);

        await GetTrackInfoAsync(rawPath, job, ct);

        var track = job.Tracks.FirstOrDefault(t => t.MainFeature);
        if (track is null)
        {
            var msg = "No main feature found by HandBrake";
            logger.LogError(msg);
            throw new InvalidOperationException(msg);
        }

        track.FileName = track.OrigFileName = filename;
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Ripping main feature to {Output}", outputFile);
        var cmd = BuildCommand(rawPath, outputFile, job, trackNumber: null, mainFeature: true);

        try
        {
            var result = await RunHandBrakeCommandAsync(cmd, ct);
            logger.LogInformation("HandBrake call successful");
            track.Ripped = true;
            await db.SaveChangesAsync(ct);
            return result;
        }
        catch
        {
            job.Errors = track.Error;
            job.Status = JobState.Failure;
            await db.SaveChangesAsync(ct);
            throw;
        }
    }

    public async Task<CliResult> TranscodeAllAsync(Job job, string rawPath, string outputPath, CancellationToken ct)
    {
        await SleepCheckAsync(ct);
        logger.LogInformation("Starting BluRay/DVD transcoding - All titles");

        await GetTrackInfoAsync(rawPath, job, ct);

        CliResult? lastResult = null;
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

            track.FileName = track.OrigFileName = $"title_{trackNo}.{ext}";
            var outputFile = Path.Combine(outputPath, track.FileName);
            await db.SaveChangesAsync(ct);

            logger.LogInformation("Transcoding title {TrackNo} to {Output}", trackNo, outputFile);
            var cmd = BuildCommand(rawPath, outputFile, job, trackNo, mainFeature: false);

            try
            {
                lastResult = await RunHandBrakeCommandAsync(cmd, ct);
                track.Ripped = true;
                await db.SaveChangesAsync(ct);
            }
            catch
            {
                await db.SaveChangesAsync(ct);
                throw;
            }
        }

        return lastResult ?? new CliResult(0, "", "", false);
    }

    private async Task SleepCheckAsync(CancellationToken ct)
    {
        var maxTranscodes = settings.Value.MaxConcurrentTranscodes;
        if (maxTranscodes <= 0)
            return;

        logger.LogInformation("Starting sleep check of HandBrakeCLI");
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var count = Process.GetProcessesByName("HandBrakeCLI").Length;
            if (count < maxTranscodes)
                break;

            logger.LogDebug("{Count} processes running. Sleeping 20s.", count);
            await Task.Delay(TimeSpan.FromSeconds(20), ct);
        }

        logger.LogInformation("Exiting sleep check of HandBrakeCLI");
    }

    private string BuildCommand(string inputPath, string outputPath, Job job, int? trackNumber, bool mainFeature)
    {
        var cmd = $"nice HandBrakeCLI -i \"{inputPath}\" -o \"{outputPath}\"";

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
            cmd += " --start-at duration:0 --stop-at duration:120";

        cmd += " 2>&1";
        return cmd;
    }

    private (string? preset, string? args) GetHbSettings(Job job)
    {
        if (job.DiscType == DiscType.Dvd)
            return (job.Config?.HbPresetDvd ?? settings.Value.HbPresetDvd, job.Config?.HbArgsDvd ?? "");

        if (job.DiscType == DiscType.Bluray)
            return (job.Config?.HbPresetBd ?? settings.Value.HbPresetBd, job.Config?.HbArgsBd ?? "");

        return (settings.Value.HbPresetDvd, "");
    }

    private async Task<CliResult> RunHandBrakeCommandAsync(string cmd, CancellationToken ct)
    {
        logger.LogDebug("Sending command: {Command}", cmd);

        var result = await runner.RunAsync("bash", $"-c \"{cmd.Replace("\"", "\\\"")}\"", timeoutMs: 7200_000, ct: ct);

        if (result.ExitCode != 0)
            logger.LogError("HandBrake failed with code {Code}: {Error}", result.ExitCode, result.StdErr);

        return result;
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
