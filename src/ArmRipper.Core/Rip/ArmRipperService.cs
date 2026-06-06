using ArmRipper.Core.Configuration;
using ArmRipper.Core.Infrastructure;
using ArmRipper.Core.Infrastructure.Data;
using ArmRipper.Core.Models;
using ArmRipper.Core.Notifications;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArmRipper.Core.Rip;

public sealed class ArmRipperService(
    ILogger<ArmRipperService> logger,
    ArmDbContext db,
    MakeMkvService makeMkv,
    IHandBrakeService handBrake,
    IFfmpegService ffmpeg,
    ICliProcessRunner runner,
    NotificationService notifications,
    IOptions<ArmSettings> settings) : IArmRipperService
{
    public async Task<string> RipVisualMediaAsync(Job job, string logFile, bool hasDupes, bool protection, CancellationToken ct = default)
    {
        var typeSubFolder = ConvertJobType(job.VideoType);
        var jobTitle = FixJobTitle(job);

        var transcodeOutPath = Path.Combine(job.Config?.TranscodePath ?? settings.Value.TranscodePath!, typeSubFolder, jobTitle);
        var finalDirectory = Path.Combine(job.Config?.CompletedPath ?? settings.Value.CompletedPath!, typeSubFolder, jobTitle);

        job.Stage ??= DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();

        transcodeOutPath = CheckForDupeFolder(hasDupes, transcodeOutPath, job);
        finalDirectory = CheckForDupeFolder(hasDupes, finalDirectory, job);

        job.Path = finalDirectory;
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Processing files to: {TranscodeOutPath}", transcodeOutPath);

        var makeMkvOutPath = Path.Combine(job.Config?.RawPath ?? settings.Value.RawPath!, jobTitle);
        var transcodeInPath = job.DevPath;
        var useMakeMkv = RipWithMkv(job, protection);

        logger.LogDebug("Using MakeMKV: {UseMakeMkv}", useMakeMkv);

        if (useMakeMkv)
        {
            if (settings.Value.TestMode && job.DiscType == DiscType.Bluray)
            {
                logger.LogInformation("Test mode: skipping MakeMKV rip for Blu-ray (whole-title rip too large)");
            }
            else
            {
                logger.LogInformation("************* Ripping disc with MakeMKV *************");
                job.Status = JobState.VideoRipping;
                await db.SaveChangesAsync(ct);

                    try
                    {
                        if (!Directory.Exists(makeMkvOutPath))
                            Directory.CreateDirectory(makeMkvOutPath);

                        var mkvTitles = settings.Value.TestMode ? "0" : "all";
                        await foreach (var _ in makeMkv.RunAsync<TInfo>(
                            ["mkv", $"dev:{job.DevPath}", mkvTitles, makeMkvOutPath],
                            MakeMkvOutputType.TInfo, ct)) { }

                        if (Directory.Exists(makeMkvOutPath))
                        {
                            foreach (var file in Directory.EnumerateFiles(makeMkvOutPath, "*.mkv"))
                            {
                                var fileName = Path.GetFileName(file);
                                var track = new Track
                                {
                                    JobId = job.Id,
                                    FileName = fileName,
                                    Source = "MakeMKV",
                                    BaseName = jobTitle,
                                };
                                db.Tracks.Add(track);
                            }
                            await db.SaveChangesAsync(ct);
                        }
                    }
                    catch (Exception mkvError)
                    {
                        logger.LogError(mkvError, "Error while running MakeMKV");
                        throw;
                    }

                    if (job.Config?.NotifyRip ?? settings.Value.NotifyRip)
                    {
                        await notifications.NotifyAsync(job, NotificationService.NotifyTitle,
                            $"{job.Title} rip complete. Starting transcode.", ct);
                    }

                    logger.LogInformation("************* Ripping with MakeMKV completed *************");
                    transcodeInPath = makeMkvOutPath;
                }
            }

        if (settings.Value.TestMode && transcodeInPath is not null && Directory.Exists(transcodeInPath))
        {
            logger.LogInformation("Test mode: trimming raw MKV files to 120 seconds");
            foreach (var file in Directory.EnumerateFiles(transcodeInPath, "*.mkv"))
            {
                var tmp = file + ".trimmed";
                var trimResult = await runner.RunAsync("ffmpeg",
                    $"-t 120 -i \"{file}\" -c copy -y \"{tmp}\"", timeoutMs: 120_000, ct: ct);
                if (trimResult.ExitCode == 0 && File.Exists(tmp))
                {
                    File.Delete(file);
                    File.Move(tmp, file);
                }
            }
        }

        await StartTranscodeAsync(job, logFile, transcodeInPath!, transcodeOutPath, protection, ct);

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

        await MoveFilesPostAsync(transcodeOutPath, job, ct);

        await ScanEmbyAsync(job, ct);

        SetPermissions(finalDirectory, job);

        DeleteRawFiles(new[] { transcodeInPath, transcodeOutPath, makeMkvOutPath }.OfType<string>().ToArray());

        await NotifyExitAsync(job, ct);

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

        job.Status = JobState.TranscodeActive;
        await db.SaveChangesAsync(ct);

        if (job.Config?.UseFfmpeg ?? settings.Value.UseFfmpeg)
        {
            logger.LogInformation("************* Starting Transcode With FFMPEG *************");
            if (RipWithMkv(job, protection) && (job.Config?.RipMethod ?? settings.Value.RipMethod) == "mkv")
            {
                logger.LogDebug("ffmpeg_mkv: {RawInPath}, {TranscodeOutPath}", rawInPath, transcodeOutPath);
                await ffmpeg.TranscodeMkvAsync(job, rawInPath, transcodeOutPath, ct);
            }
            else if (job.VideoType == "movie" && (job.Config?.MainFeature ?? settings.Value.MainFeature) && job.HasNiceTitle)
            {
                logger.LogDebug("ffmpeg_main_feature: {RawInPath}, {TranscodeOutPath}", rawInPath, transcodeOutPath);
                await ffmpeg.TranscodeMainFeatureAsync(job, rawInPath, transcodeOutPath, ct);
            }
            else
            {
                logger.LogDebug("ffmpeg_all: {RawInPath}, {TranscodeOutPath}", rawInPath, transcodeOutPath);
                await ffmpeg.TranscodeAllAsync(job, rawInPath, transcodeOutPath, ct);
            }
            logger.LogInformation("************* Finished Transcode With FFMPEG *************");

            job.Status = JobState.Active;
            await db.SaveChangesAsync(ct);
        }
        else
        {
            logger.LogInformation("************* Starting Transcode With HandBrake *************");
            if (RipWithMkv(job, protection) && (job.Config?.RipMethod ?? settings.Value.RipMethod) == "mkv")
            {
                logger.LogDebug("handbrake_mkv: {RawInPath}, {TranscodeOutPath}", rawInPath, transcodeOutPath);
                await handBrake.TranscodeMkvAsync(job, rawInPath, transcodeOutPath, ct);
            }
            else if (job.VideoType == "movie" && (job.Config?.MainFeature ?? settings.Value.MainFeature) && job.HasNiceTitle)
            {
                logger.LogDebug("handbrake_main_feature: {RawInPath}, {TranscodeOutPath}", rawInPath, transcodeOutPath);
                await handBrake.TranscodeMainFeatureAsync(job, rawInPath, transcodeOutPath, ct);
            }
            else
            {
                logger.LogDebug("handbrake_all: {RawInPath}, {TranscodeOutPath}", rawInPath, transcodeOutPath);
                await handBrake.TranscodeAllAsync(job, rawInPath, transcodeOutPath, ct);
            }
            logger.LogInformation("************* Finished Transcode With HandBrake *************");

            job.Status = JobState.Active;
            await db.SaveChangesAsync(ct);
        }
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
        if (currentJob.DiscType == DiscType.Dvd && !mainFeature && ripMethod == "mkv") return true;
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
            hbOutPath = hbOutPath + "_" + job.Stage;
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
