using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ArmMedia.OvidProvider;
using ArmMedia.OvidProvider.Fingerprint;
using ArmRipper.Core.Configuration;
using ArmRipper.Core.Infrastructure;
using ArmRipper.Core.Infrastructure.Data;
using ArmRipper.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArmRipper.Core.Rip;

public sealed partial class IdentifyService(
    ICliProcessRunner runner,
    ILoggerFactory loggerFactory,
    ArmDbContext db,
    IOptions<ArmSettings> settings,
    ISettingsService settingsService,
    IHttpClientFactory httpClientFactory,
    IDiscDbHashService discDbHashService,
    IDiscDbQueryService discDbQueryService,
    IDiscDbMappingService discDbMappingService,
    IBackgroundRipService backgroundRipService,
    OvidApiClient ovidApiClient,
    IOptions<OvidProviderOptions> ovidOptions,
    ArmMedia.Core.Abstractions.ITitleNormalizer? titleNormalizer = null) : IIdentifyService
{
    private readonly ILogger logger = loggerFactory.CreateLogger("IdentifyService");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public async Task IdentifyAsync(Job job, CancellationToken ct = default)
    {
        var devPath = job.DevPath ?? throw new InvalidOperationException($"Job {job.Id} has no DevPath");

        job.ProgressMessage = "Mounting disc...";

        var mounted = await CheckMountAsync(job, ct);

        if (mounted)
        {
            job.ProgressMessage = "Detecting disc type...";
            var mountPoint = job.MountPoint ?? throw new InvalidOperationException($"Job {job.Id} has no MountPoint after successful mount");
            job.DiscType = await DetectDiscTypeAsync(job, mountPoint, ct);
        }
        else
        {
            // CheckMountAsync already verified no media — don't bother with
            // fallback detection.  Mark the job as failed immediately;
            // there is no disc to identify.
            if (!await CheckMediaPresentAsync(devPath, ct))
            {
                job.DiscType = DiscType.Unknown;
                job.Status = JobState.Failure;
                job.Errors = "No media detected on device";
                await db.SaveChangesAsync(ct);
                logger.LogWarning(
                    "No media detected on {DevPath} — skipping identification", job.DevPath);
                return;
            }

            job.ProgressMessage = "Detecting disc type (fallback)...";
            job.DiscType = await DetectDiscTypeAsync(job, job.MountPoint, ct);
        }

        if (job.DiscType is DiscType.Dvd or DiscType.Bluray or DiscType.Uhd)
        {
            await IdentifyVideoDiscAsync(job, ct);
        }

        // ── Auto-populate season/disc from title normalization (series only) ──
        if (titleNormalizer is not null &&
            job.VideoType is VideoContentType.Series or VideoContentType.Tv &&
            !string.IsNullOrWhiteSpace(job.Title))
        {
            var norm = titleNormalizer.Normalize(job.Title);
            logger.LogDebug(
                "[IdentifyService] Title normalized: '{Title}' → season={Season}, disc={Disc}",
                job.Title, norm.Season, norm.Disc);

            if (norm.Season is int s)
            {
                job.SeasonNumberAuto = s;
                job.SeasonNumber ??= s;
            }

            if (norm.Disc is int d)
            {
                job.DiscNumberAuto = d;
                job.DiscNumber ??= d;
            }

            // Also try the label (e.g. "S3D1" or "Season 3 Disc 2")
            if (norm.Season is null && !string.IsNullOrWhiteSpace(job.Label))
            {
                var labelNorm = titleNormalizer.Normalize(job.Label);
                if (labelNorm.Season is int ls)
                {
                    job.SeasonNumberAuto = ls;
                    job.SeasonNumber ??= ls;
                }
                if (labelNorm.Disc is int ld)
                {
                    job.DiscNumberAuto = ld;
                    job.DiscNumber ??= ld;
                }
            }
        }

        job.ProgressMessage = "Computing disc fingerprint...";
        await ComputeDiscFingerprintAsync(job, ct);

        job.ProgressMessage = "Unmounting disc...";
        await UnmountAsync(job, ct);

        // Persist remaining mutations (title normalization, disc fingerprint, etc.)
        // that were set after IdentifyVideoDiscAsync's single-save boundary.
        await db.SaveChangesAsync(ct);
    }

    private async Task IdentifyVideoDiscAsync(Job job, CancellationToken ct)
    {
        logger.LogInformation("Disc identified as video");

        // Phase 1: Exact disc ID via content hash
        await QueryDiscDbAsync(job, ct);

        // Phase 2: OVID structural fingerprint + API lookup
        if (string.IsNullOrEmpty(job.OvidFingerprint))
        {
            job.ProgressMessage = "Computing OVID fingerprint...";
            await ComputeOvidFingerprintAsync(job, ct);
        }

        // If DiscDb didn't find a match, try OVID API for authoritative metadata
        if (!job.HasNiceTitle && !string.IsNullOrEmpty(job.OvidFingerprint))
        {
            await QueryOvidApiAsync(job, ct);
        }

        // Phase 3: Fallback title/metadata lookups (fill gaps only)
        await RunFallbackTitleLookupAsync(job, ct);

        // ── Accumulate-then-apply ──
        // All metadata mutations above accumulated on the in-memory `job` object.
        // Persist them in a single save to eliminate fragile partial state:
        // if the process crashes before this line, the job retains only the
        // operational state saved earlier (MountPoint, DiscType, Status/Errors).
        await db.SaveChangesAsync(ct);
    }

    private async Task QueryDiscDbAsync(Job job, CancellationToken ct)
    {
        if (!settings.Value.DiscDbEnabled || string.IsNullOrEmpty(job.MountPoint))
            return;

        job.ProgressMessage = "Querying TheDiscDb...";

        // MountPoint is already validated non-null by the guard above
        var hash = await discDbHashService.ComputeHashAsync(job.MountPoint, job.DiscType, ct);
        if (hash is not null)
        {
            job.DiscDbHash = hash;
            logger.LogInformation("DiscDb content hash computed: {Hash}", hash);

            var mapping = await discDbMappingService.GetCachedMappingAsync(hash, ct);
            if (mapping is null)
            {
                mapping = await discDbQueryService.QueryByHashAsync(hash, ct);
                if (mapping is not null)
                {
                    await discDbMappingService.SaveMappingAsync(hash, mapping, ct);
                    logger.LogInformation("DiscDb mapping cached for hash {Hash}: {MediaTitle} ({MediaYear})",
                        hash, mapping.Title, mapping.Year);
                }
            }
            else
            {
                await discDbMappingService.TouchMappingAsync(hash, ct);
                logger.LogInformation("DiscDb mapping cache hit for hash {Hash}: {MediaTitle} ({MediaYear})",
                    hash, mapping.Title, mapping.Year);
            }

            if (mapping is not null)
            {
                // DiscDb is authoritative — set title/year from exact content hash match.
                // These will not be overwritten by fallback lookups (see guards below).
                if (!string.IsNullOrEmpty(mapping.Title))
                {
                    job.Title = job.TitleAuto = mapping.Title;
                    job.HasNiceTitle = true;
                }

                // TheDiscDb returns "Series" (not "tv") for TV shows
                if (!string.IsNullOrEmpty(mapping.Type) &&
                    (mapping.Type.Equals("tv", StringComparison.OrdinalIgnoreCase) ||
                     mapping.Type.Equals("Series", StringComparison.OrdinalIgnoreCase)))
                {
                    job.VideoType = VideoContentType.Tv;
                    job.VideoTypeAuto = VideoContentType.Tv;
                }

                if (string.IsNullOrEmpty(job.YearAuto) && !string.IsNullOrEmpty(mapping.Year))
                {
                    job.Year = job.YearAuto = mapping.Year;
                }

                // Fill poster from TheDiscDb if no valid poster already found
                if ((string.IsNullOrEmpty(job.PosterUrlAuto) ||
                     job.PosterUrlAuto!.Equals("N/A", StringComparison.OrdinalIgnoreCase)) &&
                    !string.IsNullOrEmpty(mapping.ImageUrl))
                {
                    var posterUrl = BuildDiscDbImageUrl(mapping.ImageUrl);
                    job.PosterUrl = job.PosterUrlAuto = posterUrl;
                    logger.LogInformation("DiscDb poster URL set: {PosterUrl}", posterUrl);
                }
            }
        }
        else
        {
            logger.LogWarning("DiscDb hash computation returned null (unsupported disc or I/O error)");
        }
    }

    private async Task RunFallbackTitleLookupAsync(Job job, CancellationToken ct)
    {
        if (!settings.Value.GetVideoTitle)
            return;

        var identified = job.DiscType switch
        {
            DiscType.Dvd => await IdentifyDvdAsync(job, ct),
            DiscType.Bluray or DiscType.Uhd => await IdentifyBlurayAsync(job, ct),
            _ => false
        };

        if (identified)
        {
            // Always fetch supplementary metadata (poster, IMDb ID, etc.)
            // even when an authoritative source already set the title.
            // The individual field assignments inside GetVideoDetailsAsync
            // are guarded by null/empty checks so they won't overwrite
            // values already provided by DiscDb or OVID.
            job.ProgressMessage = "Fetching metadata...";
            await GetVideoDetailsAsync(job, ct);
        }
        else if (!job.HasNiceTitle)
        {
            if (!string.IsNullOrEmpty(job.Label))
            {
                job.Title = job.TitleAuto = job.Label;
                job.Warnings = "Disc not identified. Using label as title.";
                logger.LogWarning("{Warning}", job.Warnings);
            }
        }

        logger.LogInformation("Disc title post-ident: title={Title} year={Year} video_type={VideoType} disctype={DiscType}",
            job.Title, job.Year, job.VideoType, job.DiscType);
    }

    private async Task QueryOvidApiAsync(Job job, CancellationToken ct)
    {
        if (!ovidOptions.Value.Enabled)
            return;

        var fingerprint = job.OvidFingerprint;
        if (string.IsNullOrWhiteSpace(fingerprint))
            return;

        job.ProgressMessage = "Looking up OVID database...";

        logger.LogInformation("Querying OVID API for fingerprint {Fingerprint}", fingerprint);

        var record = await ovidApiClient.LookupByFingerprintAsync(fingerprint, ct);

        if (record is null || record.Release is null)
        {
            logger.LogDebug("OVID API returned no match for fingerprint {Fingerprint}", fingerprint);
            return;
        }

        // OVID is authoritative — set title/year from exact structural fingerprint match.
        // These will not be overwritten by fallback lookups (see phase 3 guards).
        if (!string.IsNullOrEmpty(record.Release.Title))
        {
            job.Title = job.TitleAuto = record.Release.Title;
            job.HasNiceTitle = true;
        }

        if (record.Release.Year.HasValue && string.IsNullOrEmpty(job.YearAuto))
        {
            job.Year = job.YearAuto = record.Release.Year.Value.ToString(CultureInfo.InvariantCulture);
        }

        if (!string.IsNullOrEmpty(record.Release.ImdbId) && string.IsNullOrEmpty(job.ImdbIdAuto))
        {
            job.ImdbId = job.ImdbIdAuto = record.Release.ImdbId;
        }

        // Map OVID content type to ARM video type
        if (job.VideoTypeAuto is null && !string.IsNullOrEmpty(record.Release.ContentType))
        {
            var videoType = record.Release.ContentType switch
            {
                "movie" => VideoContentType.Movie,
                "tvshow" => VideoContentType.Tv,
                _ => (VideoContentType?)null
            };
            job.VideoTypeAuto = videoType;
            if (videoType is { } vt)
                job.VideoType = vt;
        }

        // Cache the raw API response for later provider pipeline use
        job.OvidApiResponse = System.Text.Json.JsonSerializer.Serialize(record);

        logger.LogInformation(
            "OVID API identified disc: {Title} ({Year}), content type: {ContentType}, tmdb_id: {TmdbId}",
            record.Release.Title, record.Release.Year, record.Release.ContentType, record.Release.TmdbId);
    }

    private async Task<bool> CheckMountAsync(Job job, CancellationToken ct)
    {
        var devPath = job.DevPath ?? throw new InvalidOperationException($"Job {job.Id} has no DevPath");

        // Check whether media is actually present BEFORE any device I/O.
        // Reading sysfs (/sys/block/*/size) is purely in-kernel and can
        // never block — unlike running external commands such as findmnt
        // that may hang indefinitely when the USB bus is suspended (e.g.
        // laptop lid closed, drive in autosuspend).
        if (!await CheckMediaPresentAsync(devPath, ct))
        {
            logger.LogWarning(
                "Skipping mount for {DevPath} — no media detected (sysfs returned 0 sectors)",
                devPath);
            return false;
        }

        // Check if the disc is already mounted by reading /proc/self/mountinfo
        // directly.  This is a purely in-memory virtual file and cannot block,
        // unlike the findmnt command which can hang on suspended USB devices.
        var mountPoint = FindMountFromProc(devPath);
        if (mountPoint is not null)
        {
            logger.LogInformation("Found disc {DevPath} mounted at {MountPoint}", devPath, mountPoint);
            job.MountPoint = mountPoint;
            await ExtractDiscLabelAsync(job, ct);
            return true;
        }

        // Try to mount the disc with simple retries.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            logger.LogInformation("Mount attempt {Attempt}: trying to mount disc at {DevPath}...", attempt + 1, devPath);
            var devName = Path.GetFileName(devPath);
            var mountTarget = $"/mnt/dev/{devName}";
            Directory.CreateDirectory(mountTarget);

            // Optical discs must be mounted read-only (-o ro).  Without it,
            // the kernel rejects the mount with "Can't mount, would change RO
            // state" (dmesg) → "already mounted or mount point busy" in userspace.
            // -t udf,iso9660 tells the kernel to try UDF first (Blu-ray/DVD-Video)
            // with ISO9660 as fallback, instead of relying on auto-detection.
            var mountResult = await runner.RunAsync("mount",
                $"-t udf,iso9660 -o ro --source {devPath} --target {mountTarget}", timeoutMs: 30_000, ct: ct);

            if (mountResult.ExitCode == 0)
            {
                mountPoint = FindMountFromProc(devPath);
                if (mountPoint is not null)
                {
                    logger.LogInformation("Successfully mounted disc to {MountPoint}", mountPoint);
                    job.MountPoint = mountPoint;
                    await ExtractDiscLabelAsync(job, ct);
                    return true;
                }
            }

            // Mount failed — wait briefly before retry
            await Task.Delay(1000, ct);
        }

        if (!string.IsNullOrEmpty(job.MountPoint))
            await ExtractDiscLabelAsync(job, ct);

        logger.LogError("Disc was not and could not be mounted. Rip might fail.");
        return false;
    }

    private async Task ExtractDiscLabelAsync(Job job, CancellationToken ct)
    {
        try
        {
            var result = await runner.RunAsync("blkid",
                $"-s LABEL -o value {job.DevPath!}", timeoutMs: 5_000, ct: ct);
            if (result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StdOut))
            {
                job.Label = result.StdOut.Trim();
                logger.LogInformation("Disc label: {Label}", job.Label);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to extract disc label");
        }
    }

    /// <summary>
    /// Determines the disc type.  Tries, in order:
    /// 1. mounted-filesystem markers (VIDEO_TS/BDMV/CDA/AUDIO_TS — most authoritative),
    /// 2. udev media properties (ID_CDROM_MEDIA_* — works without a readable mount),
    /// 3. size-based classification from the sysfs sector count.
    /// </summary>
    private async Task<DiscType> DetectDiscTypeAsync(Job job, string? mountPoint, CancellationToken ct)
    {
        if (mountPoint is not null)
        {
            var markerType = GetDiscType(mountPoint);
            if (markerType != DiscType.Unknown)
                return markerType;

            // Diagnostic: record what the mounted filesystem actually shows so a
            // future "unknown" can distinguish a marker-less disc from a bad
            // mount/view (e.g. only the ISO9660 bridge of a hybrid UDF disc).
            LogMountPointContents(mountPoint);
        }

        var udevType = await DetectDiscTypeFromUdevAsync(job, ct);
        if (udevType != DiscType.Unknown)
            return udevType;

        var sizeType = await DetectDiscTypeBySizeAsync(job, ct);
        if (sizeType == DiscType.Unknown && mountPoint is not null)
        {
            logger.LogWarning(
                "Could not determine disc type for {DevPath}: no VIDEO_TS/BDMV/AUDIO_TS markers at {MountPoint}, " +
                "no udev media properties, and size-based detection failed",
                job.DevPath, mountPoint);
        }

        return sizeType;
    }

    /// <summary>
    /// Reads udev media properties for the device (ID_CDROM_MEDIA_DVD/BD and the
    /// audio track count) to classify the disc without needing a readable mount.
    /// Mirrors the original ARM <c>parse_udev</c>.  Returns Unknown when udev is
    /// unavailable or the properties are absent/stale.
    /// </summary>
    private async Task<DiscType> DetectDiscTypeFromUdevAsync(Job job, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(job.DevPath) || !File.Exists(job.DevPath))
            return DiscType.Unknown;

        try
        {
            var result = await runner.RunAsync("udevadm",
                $"info --query=property {job.DevPath}", timeoutMs: 10_000, ct: ct);
            if (result.ExitCode != 0)
                return DiscType.Unknown;

            DiscType type = DiscType.Unknown;
            foreach (var line in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.StartsWith("ID_CDROM_MEDIA_BD=") && line.EndsWith("1"))
                    type = DiscType.Bluray;
                else if (line.StartsWith("ID_CDROM_MEDIA_DVD=") && line.EndsWith("1"))
                    type = DiscType.Dvd;
                else if (line.StartsWith("ID_CDROM_MEDIA_TRACK_COUNT_AUDIO="))
                {
                    var value = line["ID_CDROM_MEDIA_TRACK_COUNT_AUDIO=".Length..].Trim('"');
                    if (int.TryParse(value, out var count) && count > 0)
                        type = DiscType.Music;
                }
            }

            if (type != DiscType.Unknown)
                logger.LogInformation("Disc type identified from udev media properties: {DiscType}", type);

            return type;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to detect disc type from udev media properties");
            return DiscType.Unknown;
        }
    }

    /// <summary>
    /// Logs the top-level entries of the mount point for diagnostics when the
    /// filesystem markers yield no disc type.
    /// </summary>
    private void LogMountPointContents(string mountPoint)
    {
        try
        {
            if (!Directory.Exists(mountPoint))
            {
                logger.LogWarning("Disc type detection: mount point {MountPoint} no longer exists", mountPoint);
                return;
            }

            var entries = Directory.EnumerateFileSystemEntries(mountPoint)
                .Take(50)
                .Select(Path.GetFileName)
                .ToArray();
            logger.LogDebug(
                "Disc type detection: top-level entries at {MountPoint}: {Entries}",
                mountPoint, string.Join(", ", entries));
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to enumerate mount point {MountPoint} for diagnostics", mountPoint);
        }
    }

    private async Task<DiscType> DetectDiscTypeBySizeAsync(Job job, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(job.DevPath) || !File.Exists(job.DevPath))
            {
                logger.LogWarning("Size-based detection skipped: device path {DevPath} is not available", job.DevPath);
                return DiscType.Unknown;
            }

            // Use sysfs to read sector count — works even on encrypted discs
            var devName = Path.GetFileName(job.DevPath!.TrimEnd('/'));
            var sysfsPath = $"/sys/block/{devName}/size";
            if (!File.Exists(sysfsPath))
                return DiscType.Unknown;

            var content = await File.ReadAllTextAsync(sysfsPath, ct);
            if (!long.TryParse(content.Trim(), out var sectors))
                return DiscType.Unknown;

            if (sectors <= 0)
                return DiscType.Unknown;

            var bytes = sectors * 512L;

            // BD single layer ~25GB, dual layer ~50GB; DVD max is ~8.5GB
            if (bytes > 15_000_000_000L)
            {
                logger.LogInformation("Disc size {Size}GB exceeds DVD limit, identified as Blu-ray (fallback)",
                    bytes / 1_000_000_000);
                return DiscType.Bluray;
            }

            if (bytes > 4_000_000_000L)
            {
                logger.LogInformation("Disc size {Size}GB, identified as DVD (fallback)",
                    bytes / 1_000_000_000);
                return DiscType.Dvd;
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to detect disc type by size");
        }

        return DiscType.Unknown;
    }

    /// <summary>
    /// Checks /proc/self/mountinfo for the mount point of the given device.
    /// This is a purely in-memory virtual file and will never block on I/O,
    /// unlike the findmnt command which can hang indefinitely when the USB
    /// bus is suspended (e.g. laptop lid closed).
    /// </summary>
    private static string? FindMountFromProc(string devPath)
    {
        // /proc/self/mountinfo format (see proc(5)):
        // 36 35 98:0 / /mnt/point rw,noatime - ext3 /dev/sda rw
        // ^1 ^2  ^3  ^4    ^5        ^6         ^7 ^8   ^9      ^10
        // Fields (space-separated, after optional "-"):
        //   1: mount ID
        //   2: parent ID
        //   3: major:minor
        //   4: root
        //   5: mount point
        //   6: mount options
        //   7: optional fields (terminated by "-")
        //   8: filesystem type
        //   9: mount source (device path)
        //  10: super options
        //
        // We normalize the device path to resolve symlinks (e.g. /dev/sr0
        // may appear as /dev/sr0 or /dev/sg2 in mountinfo).

        // Resolve the real path of the device
        string realDevPath;
        try
        {
            realDevPath = System.IO.File.ResolveLinkTarget(devPath, false)?.FullName ?? devPath;
        }
        catch
        {
            realDevPath = devPath;
        }

        var mountInfoPath = "/proc/self/mountinfo";
        if (!System.IO.File.Exists(mountInfoPath))
            return null;

        try
        {
            var lines = System.IO.File.ReadAllLines(mountInfoPath);
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                // Split on spaces; find the "-" separator that marks the end
                // of the optional fields and start of the type/device fields.
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var dashIndex = Array.IndexOf(parts, "-");
                if (dashIndex < 0 || dashIndex + 3 >= parts.Length)
                    continue;

                // The mount source is 2 fields after the "-"
                var mountSource = parts[dashIndex + 2];

                // Compare with both the original and resolved device path
                if (!string.Equals(mountSource, devPath, StringComparison.Ordinal) &&
                    !string.Equals(mountSource, realDevPath, StringComparison.Ordinal))
                    continue;

                // The mount point is field index 4 (0-based) before the "-"
                var mountPoint = parts[4];
                if (!string.IsNullOrEmpty(mountPoint) && Directory.Exists(mountPoint))
                    return mountPoint;
            }
        }
        catch
        {
            // Ignore I/O errors on /proc (should never happen)
        }

        return null;
    }

    private static DiscType GetDiscType(string mountPoint)
    {
        var videoTs = Path.Combine(mountPoint, "VIDEO_TS");
        if (Directory.Exists(videoTs) || FindOnDisc("VIDEO_TS", mountPoint))
            return DiscType.Dvd;

        var bdmv = Path.Combine(mountPoint, "BDMV");
        if (Directory.Exists(bdmv) || FindOnDisc("BDMV", mountPoint))
            return DiscType.Bluray;

        if (FindOnDisc("CDA", mountPoint))
            return DiscType.Music;

        // AUDIO_TS is a data marker on DVD-Video hybrid discs (matches the
        // original ARM get_disc_type).  VIDEO_TS/BDMV are checked first, so a
        // real video disc is never misclassified as data.
        var audioTs = Path.Combine(mountPoint, "AUDIO_TS");
        if (Directory.Exists(audioTs) || FindOnDisc("AUDIO_TS", mountPoint))
            return DiscType.Data;

        return DiscType.Unknown;
    }

    private static bool FindOnDisc(string fileName, string searchPath)
    {
        if (!Directory.Exists(searchPath))
            return false;

        try
        {
            foreach (var dir in Directory.EnumerateDirectories(searchPath, "*", SearchOption.TopDirectoryOnly))
            {
                if (File.Exists(Path.Combine(dir, fileName)))
                    return true;
            }

            return File.Exists(Path.Combine(searchPath, fileName));
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task<bool> IdentifyDvdAsync(Job job, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(job.Label))
            job.Label = "not identified";

        try
        {
            var crc64 = (string?)null;
            if (string.IsNullOrEmpty(job.MountPoint))
            {
                logger.LogWarning("Disc not mounted, skipping CRC64 identification");
            }
            else
            {
                job.ProgressMessage = "Computing CRC64 hash...";
                crc64 = await ComputeDvdCrc64Async(job.MountPoint, ct);
                logger.LogInformation("DVD CRC64 hash is: {Crc64}", crc64);
                job.CrcId = crc64;
            }

            if (crc64 is not null)
            {
                var url = $"https://1337server.pythonanywhere.com/api/v1/?mode=s&crc64={crc64}";
                var httpClient = httpClientFactory.CreateClient("IdentifyService");
                var response = await httpClient.GetStringAsync(url, ct);
                var armApiResult = JsonSerializer.Deserialize<ArmApiResponse>(response, JsonOptions);

                if (armApiResult?.Success == true && armApiResult.Results?.Count > 0)
                {
                    var first = armApiResult.Results["0"];
                    logger.LogInformation("Found CRC64 id from online API: title={Title}", first.Title);

                    // CRC64/ARM API is a fallback — only fill fields not already set
                    // by authoritative exact-disc-ID sources (DiscDb, OVID).
                    if (string.IsNullOrEmpty(job.TitleAuto))
                    {
                        job.Title = job.TitleAuto = first.Title;
                        job.HasNiceTitle = true;
                    }

                    if (string.IsNullOrEmpty(job.YearAuto))
                        job.Year = job.YearAuto = first.Year;

                    if (string.IsNullOrEmpty(job.ImdbIdAuto))
                        job.ImdbId = job.ImdbIdAuto = first.ImdbId;

                    if (job.VideoTypeAuto is null)
                    {
                        var videoType = ParseVideoType(first.VideoType);
                        job.VideoTypeAuto = videoType;
                        if (videoType is { } vt)
                            job.VideoType = vt;
                    }

                    if (string.IsNullOrEmpty(job.PosterUrlAuto) ||
                        job.PosterUrlAuto!.Equals("N/A", StringComparison.OrdinalIgnoreCase))
                        job.PosterUrl = job.PosterUrlAuto = first.PosterUrl;

                    // CRC already exists in the remote DB — mark as submitted so
                    // the submit service skips it rather than trying to re-upload.
                    job.MarkStageComplete(RipStage.CrcSubmitted);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "DVD identification failed");
        }

        // Track 99 detection via lsdvd
        await DetectTrack99Async(job, ct);

        // Fallback: use label as title when CRC64 lookup didn't find a match
        if (string.IsNullOrEmpty(job.Title) && !string.IsNullOrEmpty(job.Label))
            job.Title = job.TitleAuto = job.Label;

        // Extract poster from disc while it's still mounted
        await SaveDiscPosterAsync(job, ct);

        return true;
    }

    private async Task DetectTrack99Async(Job job, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(job.DevPath))
            {
                logger.LogDebug("Track 99 detection skipped: no device path");
                return;
            }

            var result = await runner.RunAsync("lsdvd", $"-Oy {job.DevPath}", timeoutMs: 30_000, ct: ct);
            if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StdOut))
            {
                logger.LogDebug("lsdvd returned no output for {DevPath}", job.DevPath);
                return;
            }

            // Count track entries — each track has a 'length' field in the Python dict
            var output = result.StdOut;
            var trackCount = 0;
            var index = 0;
            while ((index = output.IndexOf("'length'", index, StringComparison.Ordinal)) >= 0)
            {
                trackCount++;
                index += 8; // advance past 'length'
            }

            logger.LogInformation("lsdvd detected {TrackCount} tracks on {DevPath}", trackCount, job.DevPath);

            if (trackCount == 99)
            {
                job.HasTrack99 = true;
                logger.LogWarning("Track 99 disc detected on {DevPath}", job.DevPath);

                var prevent99 = job.Config?.Prevent99 ?? settings.Value.Prevent99;
                if (prevent99)
                {
                    var msg = $"Track 99 disc found on {job.DevPath} and PREVENT_99 is enabled. Aborting.";
                    logger.LogError(msg);
                    job.Status = JobState.Failure;
                    job.Errors = msg;
                    await db.SaveChangesAsync(ct);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to detect track 99 via lsdvd on {DevPath}", job.DevPath);
        }
    }

    private async Task SaveDiscPosterAsync(Job job, CancellationToken ct)
    {
        if (job.DiscType != DiscType.Dvd || string.IsNullOrEmpty(job.MountPoint))
            return;

        try
        {
            var typeSubFolder = ArmRipperService.ConvertJobType(job.VideoType);
            var jobTitle = ArmRipperService.FixJobTitle(job);
            var completedPath = ArmPaths.GetCompletedPath(settings.Value);
            var finalDir = Path.Combine(completedPath, typeSubFolder, jobTitle);
            Directory.CreateDirectory(finalDir);

            var posterFiles = new[] { "JACKET_P/J00___5L.MP2", "JACKET_P/J00___6L.MP2" };
            foreach (var posterFile in posterFiles)
            {
                var posterSrc = Path.Combine(job.MountPoint, posterFile);
                if (File.Exists(posterSrc))
                {
                    var posterDst = Path.Combine(finalDir, "poster.png");
                    logger.LogInformation("Converting {PosterSrc} to poster", posterSrc);
                    // Respect the configured ffmpeg binary (same as FfmpegService).
                    var ffmpegCli = settings.Value.FfmpegCli;
                    if (string.IsNullOrWhiteSpace(ffmpegCli))
                        ffmpegCli = "ffmpeg";
                    await runner.RunAsync(ffmpegCli, $"-y -i \"{posterSrc}\" \"{posterDst}\"", timeoutMs: 30_000, ct: ct);
                    job.PosterSavedPath = posterDst;
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to save DVD poster for job {JobId}", job.Id);
        }
    }

    private async Task ComputeOvidFingerprintAsync(Job job, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(job.MountPoint))
        {
            logger.LogDebug("OVID fingerprint skipped: no mount point");
            return;
        }

        try
        {
            var format = job.DiscType switch
            {
                DiscType.Dvd => OvidDiscFormat.Dvd,
                DiscType.Bluray => OvidDiscFormat.Bluray,
                _ => (OvidDiscFormat?)null
            };

            if (format is null)
            {
                logger.LogDebug("OVID fingerprint skipped: unsupported disc type {DiscType}", job.DiscType);
                return;
            }

            var ovidDisc = new OvidDisc(
                loggerFactory.CreateLogger<OvidDisc>());

            var result = await ovidDisc.ComputeAsync(job.MountPoint, format.Value, ct);

            if (result.IsSuccess)
            {
                job.OvidFingerprint = result.Fingerprint;
                logger.LogInformation("OVID fingerprint: {Fingerprint}", result.Fingerprint);
            }
            else
            {
                logger.LogWarning("OVID fingerprint computation returned no result at {MountPoint}", job.MountPoint);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "OVID fingerprint computation failed at {MountPoint}", job.MountPoint);
        }
    }

    private Task<string> ComputeDvdCrc64Async(string mountPoint, CancellationToken ct)
    {
        return Task.Run(() => DvdCrc64.Compute(mountPoint), ct);
    }

    private async Task<bool> IdentifyBlurayAsync(Job job, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(job.MountPoint))
        {
            logger.LogWarning("Blu-ray identification skipped: mount point is unavailable for {DevPath}", job.DevPath);

            if (!string.IsNullOrWhiteSpace(job.Label) && string.IsNullOrEmpty(job.TitleAuto))
            {
                var blurayTitle = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(
                    job.Label.Replace("_", " ").ToLowerInvariant());
                job.Title = job.TitleAuto = blurayTitle;
                job.Year = "";
                return true;
            }

            return !string.IsNullOrEmpty(job.TitleAuto);
        }

        var bdmtPath = Path.Combine(job.MountPoint, "BDMV", "META", "DL", "bdmt_eng.xml");

        try
        {
            var xml = await File.ReadAllTextAsync(bdmtPath, ct);
            var doc = XDocument.Parse(xml);
            var ns = doc.Root?.GetDefaultNamespace() ?? XNamespace.None;
            var diNs = doc.Root?.GetNamespaceOfPrefix("di") ?? XNamespace.None;

            var title = doc.Descendants(diNs + "title").FirstOrDefault()?.Value;
            if (string.IsNullOrEmpty(title))
                title = doc.Descendants(ns + "title").FirstOrDefault()?.Value;
            if (string.IsNullOrEmpty(title))
                title = job.Label;

            var fileInfo = new FileInfo(bdmtPath);
            var year = fileInfo.LastWriteTime.ToString("yyyy", CultureInfo.InvariantCulture);

            title = RemoveBluraySuffixes(title ?? "");
            title = CleanForFilename(title);

            // Only set title if not already populated by an authoritative source (DiscDb/OVID)
            if (string.IsNullOrEmpty(job.TitleAuto))
            {
                job.Title = job.TitleAuto = title;
                // BD-MT XML is studio-provided disc metadata — authoritative enough
                // to treat as a "nice" title.
                job.HasNiceTitle = true;
            }

            if (string.IsNullOrEmpty(job.YearAuto))
                job.Year = job.YearAuto = year;

            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to parse bdmt_eng.xml for Bluray identification");

            if (!string.IsNullOrEmpty(job.Label) && string.IsNullOrEmpty(job.TitleAuto))
            {
                var blurayTitle = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(
                    job.Label.Replace("_", " ").ToLowerInvariant());
                job.Title = job.TitleAuto = blurayTitle;
                job.Year = "";
                return true;
            }

            return false;
        }
    }

    private static string RemoveBluraySuffixes(string title)
    {
        title = title.Replace(" - Blu-rayTM", "");
        title = title.Replace(" Blu-rayTM", "");
        title = title.Replace(" - BLU-RAYTM", "");
        title = title.Replace(" - BLU-RAY", "");
        title = title.Replace(" - Blu-ray", "");
        return title;
    }

    private async Task GetVideoDetailsAsync(Job job, CancellationToken ct)
    {
        var effective = await settingsService.GetEffectiveAsync(ct);
        var title = job.Title;
        if (string.IsNullOrEmpty(title) || title == "not identified")
        {
            logger.LogInformation("Disc couldn't be identified");
            return;
        }

        // Note: the outer caller (RunFallbackTitleLookupAsync) always invokes this
        // method when identified=true, regardless of HasNiceTitle. The individual
        // field assignments below are guarded by null/empty checks, so they will
        // not overwrite values already set by authoritative sources (DiscDb, OVID).
        var searchTitle = Regex.Replace(title.Trim(), "[_ ]", "+");
        // job.Year can be a year RANGE for TV series (e.g. "2005–2014" for
        // How I Met Your Mother). OMDB (y=) and TMDB (year=) only accept a
        // single year, so use the FIRST 4-digit year (the series' start year).
        // Stripping all non-digits here would concatenate the range into a
        // garbage value like "20052014" that makes the search fail.
        var year = string.IsNullOrEmpty(job.Year) ? "" : Regex.Match(job.Year, @"\d{4}").Value;

        logger.LogDebug("Calling webservice with title: {Title} and year: {Year}", searchTitle, year);
        var response = await IdentifyLoopAsync(job, searchTitle, year, ct);

        // Parse the search result to extract video type and other metadata
        if (response is not null)
        {
            try
            {
                if (response.RootElement.TryGetProperty("Search", out var search) &&
                    search.GetArrayLength() > 0)
                {
                    var first = search[0];
                    var resultType = first.TryGetProperty("Type", out var t) ? t.GetString() : null;
                    var resultTitle = first.TryGetProperty("Title", out var tl) ? tl.GetString() : null;
                    var resultYear = first.TryGetProperty("Year", out var yr) ? yr.GetString() : null;
                    var resultImdb = first.TryGetProperty("imdbID", out var im) ? im.GetString() : null;
                    var resultPoster = first.TryGetProperty("Poster", out var po) ? po.GetString() : null;

                    // Only set VideoType if not already determined by authoritative source
                    if (!string.IsNullOrEmpty(resultType) && job.VideoTypeAuto is null)
                    {
                        var videoType = resultType switch
                        {
                            "movie" => VideoContentType.Movie,
                            "series" => VideoContentType.Series,
                            "episode" => VideoContentType.Episode,
                            _ => (VideoContentType?)null
                        };
                        job.VideoTypeAuto = videoType;
                        if (videoType is { } vt)
                            job.VideoType = vt;
                    }

                    // Update title/year from search result if ARM API didn't provide them.
                    // The title is only replaced when the current one came from a low-confidence
                    // fallback (label/"not identified") and no manual override exists — values
                    // set by authoritative sources (DiscDb, OVID, CRC64, BD-MT) are preserved.
                    // HasNiceTitle is intentionally left unchanged so the user can confirm the
                    // search-derived title via the "Approve Title" button on the job page.
                    if (!job.HasNiceTitle && string.IsNullOrEmpty(job.TitleManual) &&
                        !string.IsNullOrEmpty(resultTitle) &&
                        !resultTitle.Equals("N/A", StringComparison.OrdinalIgnoreCase))
                        job.Title = job.TitleAuto = resultTitle;

                    if (string.IsNullOrEmpty(job.YearAuto) && !string.IsNullOrEmpty(resultYear))
                        job.Year = job.YearAuto = resultYear;

                    if (string.IsNullOrEmpty(job.ImdbIdAuto) && !string.IsNullOrEmpty(resultImdb))
                        job.ImdbId = job.ImdbIdAuto = resultImdb;

                    if (string.IsNullOrEmpty(resultPoster))
                        { }
                    else if (string.IsNullOrEmpty(job.PosterUrlAuto) ||
                             job.PosterUrlAuto.Equals("N/A", StringComparison.OrdinalIgnoreCase))
                    {
                        job.PosterUrl = job.PosterUrlAuto = resultPoster;
                    }

                    // Try to get full details via IMDb ID lookup for richer metadata
                    if (!string.IsNullOrEmpty(resultImdb))
                    {
                        try
                        {
                            var apiKey = effective.OmdbApiKey;
                            if (!string.IsNullOrEmpty(apiKey))
                            {
                                var httpClient = httpClientFactory.CreateClient("IdentifyService");
                                var detailUrl = $"https://www.omdbapi.com/?i={resultImdb}&apikey={apiKey}&plot=full";
                                var detailJson = await httpClient.GetStringAsync(detailUrl, ct);
                                var detailDoc = JsonDocument.Parse(detailJson);
                                if (detailDoc.RootElement.TryGetProperty("Response", out var resp) && resp.GetString() == "True")
                                {
                                    if (string.IsNullOrEmpty(job.YearAuto) &&
                                        detailDoc.RootElement.TryGetProperty("Year", out var detailYear))
                                        job.Year = job.YearAuto = detailYear.GetString();

                                    if (string.IsNullOrEmpty(job.PosterUrlAuto) ||
                                        job.PosterUrlAuto.Equals("N/A", StringComparison.OrdinalIgnoreCase))
                                    {
                                        if (detailDoc.RootElement.TryGetProperty("Poster", out var detailPoster) &&
                                            !string.IsNullOrEmpty(detailPoster.GetString()) &&
                                            !string.Equals(detailPoster.GetString(), "N/A", StringComparison.OrdinalIgnoreCase))
                                            job.PosterUrl = job.PosterUrlAuto = detailPoster.GetString();
                                    }
                                }
                            }
                        }
                        catch (Exception ex) { logger.LogDebug(ex, "Failed to parse metadata detail result"); }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to parse metadata search result");
            }
        }
    }

    private async Task<JsonDocument?> IdentifyLoopAsync(Job job, string title, string year, CancellationToken ct)
    {
        JsonDocument? response = null;
        const int maxAttempts = 8;
        var attempts = 0;

        if (!string.IsNullOrEmpty(year))
        {
            response = await TryWithYearAsync(job, title, year, ct);
            if (response is null)
            {
                var prevYear = (int.Parse(year) - 1).ToString();
                response = await CallMetadataProviderAsync(job, title, prevYear, ct);
            }
        }

        if (response is null)
            response = await CallMetadataProviderAsync(job, title, null, ct);

        while (response is null && title.Contains('-') && attempts < maxAttempts)
        {
            attempts++;
            title = title[..title.LastIndexOf('-')].TrimEnd('+');
            response = await CallMetadataProviderAsync(job, title, string.IsNullOrEmpty(year) ? null : year, ct);
        }

        while (response is null && title.Contains('+') && attempts < maxAttempts)
        {
            attempts++;
            title = title[..title.LastIndexOf('+')].TrimEnd('+');
            response = await CallMetadataProviderAsync(job, title, string.IsNullOrEmpty(year) ? null : year, ct);
            if (response is null)
                response = await CallMetadataProviderAsync(job, title, null, ct);
        }

        if (response is null && attempts >= maxAttempts)
        {
            logger.LogDebug("IdentifyLoopAsync: reached max {MaxAttempts} attempts for title={Title}", maxAttempts, title);
        }

        return response;
    }

    private async Task<JsonDocument?> TryWithYearAsync(Job job, string title, string year, CancellationToken ct)
    {
        return await CallMetadataProviderAsync(job, title, year, ct);
    }

    private async Task<JsonDocument?> CallMetadataProviderAsync(Job job, string title, string? year, CancellationToken ct)
    {
        var effective = await settingsService.GetEffectiveAsync(ct);
        var provider = effective.MetadataProvider?.ToLowerInvariant();
        return provider switch
        {
            "tmdb" => await TmdbSearchAsync(title, year, effective.TmdbApiKey, ct),
            "omdb" => await OmdbSearchAsync(title, year, effective.OmdbApiKey, ct),
            _ => null
        };
    }

    private async Task<JsonDocument?> OmdbSearchAsync(string title, string? year, string? apiKey, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(apiKey))
            return null;

        var url = string.IsNullOrEmpty(year)
            ? $"https://www.omdbapi.com/?s={title}&r=json&apikey={apiKey}"
            : $"https://www.omdbapi.com/?s={title}&y={year}&r=json&apikey={apiKey}";

        try
        {
            var httpClient = httpClientFactory.CreateClient("IdentifyService");
            var response = await httpClient.GetStringAsync(url, ct);
            var doc = JsonDocument.Parse(response);
            var root = doc.RootElement;

            if (root.TryGetProperty("Error", out _) ||
                (root.TryGetProperty("Response", out var resp) && resp.GetString() == "False"))
                return null;

            return doc;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "OMDB API call failed");
            return null;
        }
    }

    private async Task<JsonDocument?> TmdbSearchAsync(string title, string? year, string? apiKey, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(apiKey))
            return null;

        try
        {
            var httpClient = httpClientFactory.CreateClient("IdentifyService");

            // Search movies
            var movieUrl = string.IsNullOrEmpty(year)
                ? $"https://api.themoviedb.org/3/search/movie?api_key={apiKey}&query={title}"
                : $"https://api.themoviedb.org/3/search/movie?api_key={apiKey}&query={title}&year={year}";

            var response = await httpClient.GetStringAsync(movieUrl, ct);
            var movieResults = JsonDocument.Parse(response);
            var totalResults = movieResults.RootElement.GetProperty("total_results").GetInt32();

            if (totalResults > 0)
                return ConvertTmdbToOmdb(movieResults, "movie");

            // Search TV
            var tvUrl = $"https://api.themoviedb.org/3/search/tv?api_key={apiKey}&query={title}";
            response = await httpClient.GetStringAsync(tvUrl, ct);
            var tvResults = JsonDocument.Parse(response);
            totalResults = tvResults.RootElement.GetProperty("total_results").GetInt32();

            return totalResults > 0 ? ConvertTmdbToOmdb(tvResults, "series") : null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "TMDB API call failed");
            return null;
        }
    }

    private static JsonDocument? ConvertTmdbToOmdb(JsonDocument tmdbResults, string mediaType)
    {
        var posterBase = "https://image.tmdb.org/t/p/original";
        var results = tmdbResults.RootElement.GetProperty("results");

        var searchArray = new List<Dictionary<string, object?>>();

        foreach (var item in results.EnumerateArray())
        {
            var title = item.TryGetProperty("title", out var t) ? t.GetString()
                : item.TryGetProperty("name", out var n) ? n.GetString()
                : "Unknown";

            var releaseDate = item.TryGetProperty("release_date", out var rd) ? rd.GetString()
                : item.TryGetProperty("first_air_date", out var fad) ? fad.GetString()
                : "";

            var year = !string.IsNullOrEmpty(releaseDate) && releaseDate!.Length >= 4
                ? releaseDate[..4] : "";

            var posterPath = item.TryGetProperty("poster_path", out var pp) ? pp.GetString() : null;
            var poster = posterPath is not null ? $"{posterBase}{posterPath}" : "";

            searchArray.Add(new()
            {
                ["Title"] = title,
                ["Year"] = year,
                ["Poster"] = poster,
                ["Type"] = mediaType,
                ["imdbID"] = null
            });
        }

        var wrapper = new Dictionary<string, object?>
        {
            ["Search"] = searchArray
        };

        var json = JsonSerializer.Serialize(wrapper);
        return JsonDocument.Parse(json);
    }

    private async Task ComputeDiscFingerprintAsync(Job job, CancellationToken ct)
    {
        var label = job.Label;
        if (string.IsNullOrEmpty(label))
        {
            logger.LogDebug("Cannot compute disc fingerprint: no label");
            return;
        }

        try
        {
            var devName = Path.GetFileName(job.DevPath!.TrimEnd('/'));
            var sysfsPath = $"/sys/block/{devName}/size";
            long sectors = 0;
            if (File.Exists(sysfsPath))
            {
                var content = await File.ReadAllTextAsync(sysfsPath, ct);
                long.TryParse(content.Trim(), out sectors);
            }

            if (sectors == 0)
            {
                logger.LogDebug("Cannot compute disc fingerprint: sector count is 0");
                return;
            }

            job.DiscFingerprint = $"{label}::{sectors}";
            logger.LogInformation("Disc fingerprint: {Fingerprint}", job.DiscFingerprint);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to compute disc fingerprint");
        }
    }

    private async Task UnmountAsync(Job job, CancellationToken ct)
    {
        var devPath = job.DevPath ?? throw new InvalidOperationException($"Job {job.Id} has no DevPath");
        try
        {
            await runner.RunAsync("umount", devPath, timeoutMs: 10_000, ct: ct);
            logger.LogInformation("Disc unmounted from {DevPath}", devPath);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to unmount disc");
        }
    }

    public async Task EjectAsync(Job job, CancellationToken ct)
    {
        var devPath = job.DevPath ?? throw new InvalidOperationException($"Job {job.Id} has no DevPath");

        if (!settings.Value.AutoEject)
            return;

        // Don't call `eject` if no media is present — on some drives the
        // CDROMEJECT ioctl toggles the tray (closes it when already open)
        // or returns a confusing success code for an already-open tray.
        if (!await CheckMediaPresentAsync(devPath, ct))
        {
            logger.LogDebug(
                "Skipping EjectAsync for {DevPath} — no media detected", devPath);
            return;
        }

        try
        {
            await runner.RunAsync("umount", devPath, timeoutMs: 10_000, ct: ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to unmount disc");
        }

        try
        {
            await runner.RunAsync("eject", devPath, timeoutMs: 10_000, ct: ct);
            logger.LogInformation("Disc ejected from {DevPath}", devPath);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to eject disc from {DevPath}", devPath);
        }
        finally
        {
            // Record eject cooldown immediately so the event-driven monitor
            // doesn't re-trigger a rip when the drive's firmware auto-closes
            // the tray (common on many optical drives).  Without this, the
            // cooldown would only be set in BackgroundRipService.StartRip's
            // finally block, which runs after the entire pipeline finishes
            // (including transcode, which can take hours).
            backgroundRipService.RecordManualEject(devPath);
        }
    }

    /// <summary>
    /// Check whether sysfs reports readable media on the device.
    /// Reads <c>/sys/block/{devName}/size</c> which is kernel-cached and
    /// does NOT issue SCSI commands — safe to call without closing the tray.
    /// Returns <c>true</c> if the sector count is &gt; 0 (media present).
    /// </summary>
    private static async Task<bool> CheckMediaPresentAsync(string devPath, CancellationToken ct)
    {
        try
        {
            var devName = Path.GetFileName(devPath.TrimEnd('/'));
            var sysfsPath = $"/sys/block/{devName}/size";
            if (!File.Exists(sysfsPath))
                return false;

            var content = (await File.ReadAllTextAsync(sysfsPath, ct)).Trim();
            if (long.TryParse(content, out var size))
                return size > 0;

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static string CleanForFilename(string input)
    {
        var result = Regex.Replace(input, @"\[.*?\]", "");
        result = Regex.Replace(result, @"\s+", "-");
        result = result.Replace(" : ", " - ");
        result = result.Replace(':', '-');
        result = result.Replace("&", "and");
        result = result.Replace("\\", " - ");
        result = result.Replace(" ", " - ");
        result = result.Trim();
        return Regex.Replace(result, @"[^\w.() -]", "");
    }

    private record ArmApiResponse
    {
        public bool Success { get; init; }
        public Dictionary<string, ArmApiResult>? Results { get; init; }
    }

    private record ArmApiResult
    {
        public string Title { get; init; } = "";
        public string Year { get; init; } = "";
        public string ImdbId { get; init; } = "";
        public string VideoType { get; init; } = "";
        public string PosterUrl { get; init; } = "";
    }

    private static VideoContentType? ParseVideoType(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return null;
        return Enum.TryParse<VideoContentType>(value, ignoreCase: true, out var parsed) ? parsed : null;
    }

    /// <summary>
    /// Constructs a full image URL from a TheDiscDb relative imageUrl.
    /// Example: "Movie/freaky-friday-2003/cover.jpg" → "https://thediscdb.com/images/Movie/freaky-friday-2003/cover.jpg"
    /// </summary>
    private static string BuildDiscDbImageUrl(string? imageUrl)
    {
        const string cdnBase = "https://thediscdb.com/images/";
        if (string.IsNullOrWhiteSpace(imageUrl))
            return string.Empty;

        return imageUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? imageUrl
            : cdnBase + imageUrl.TrimStart('/');
    }
}
