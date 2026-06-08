using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ArmRipper.Core.Configuration;
using ArmRipper.Core.Infrastructure;
using ArmRipper.Core.Infrastructure.Data;
using ArmRipper.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArmRipper.Core.Rip;

public sealed partial class IdentifyService(
    ICliProcessRunner runner,
    ILogger<IdentifyService> logger,
    ArmDbContext db,
    IOptions<ArmSettings> settings) : IIdentifyService
{
    public async Task IdentifyAsync(Job job, CancellationToken ct = default)
    {
        var mounted = await CheckMountAsync(job, ct);

        if (mounted)
        {
            job.DiscType = GetDiscType(job.MountPoint!);
        }
        else
        {
            job.DiscType = await DetectDiscTypeFallbackAsync(job, ct);
        }

        if (job.DiscType is DiscType.Dvd or DiscType.Bluray)
        {
            logger.LogInformation("Disc identified as video");

            if (settings.Value.GetVideoTitle)
            {
                var identified = job.DiscType switch
                {
                    DiscType.Dvd => await IdentifyDvdAsync(job, ct),
                    DiscType.Bluray => await IdentifyBlurayAsync(job, ct),
                    _ => false
                };

                if (identified)
                    await GetVideoDetailsAsync(job, ct);
                else
                    job.HasNiceTitle = false;

                await db.SaveChangesAsync(ct);

                logger.LogInformation("Disc title post-ident: title={Title} year={Year} video_type={VideoType} disctype={DiscType}",
                    job.Title, job.Year, job.VideoType, job.DiscType);
            }
        }

        await ComputeDiscFingerprintAsync(job, ct);

        await UnmountAsync(job, ct);
    }

    private async Task<bool> CheckMountAsync(Job job, CancellationToken ct)
    {
        var mountPoint = await FindMountAsync(job.DevPath!, ct);
        if (mountPoint is not null)
        {
            logger.LogInformation("Found disc {DevPath} mounted at {MountPoint}", job.DevPath, mountPoint);
            job.MountPoint = mountPoint;
            await ExtractDiscLabelAsync(job, ct);
            return true;
        }

        logger.LogInformation("Trying to mount disc at {DevPath}...", job.DevPath);
        var devName = Path.GetFileName(job.DevPath);
        var mountTarget = $"/mnt/dev/{devName}";
        Directory.CreateDirectory(mountTarget);
        await runner.RunAsync("mount", $"--source {job.DevPath!} --target {mountTarget}", timeoutMs: 30_000, ct: ct);

        mountPoint = await FindMountAsync(job.DevPath!, ct);
        if (mountPoint is not null)
        {
            logger.LogInformation("Successfully mounted disc to {MountPoint}", mountPoint);
            job.MountPoint = mountPoint;
            await ExtractDiscLabelAsync(job, ct);
            return true;
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

    private async Task<DiscType> DetectDiscTypeFallbackAsync(Job job, CancellationToken ct)
    {
        try
        {
            // Use sysfs to read sector count — works even on encrypted discs
            var devName = Path.GetFileName(job.DevPath!.TrimEnd('/'));
            var sysfsPath = $"/sys/block/{devName}/size";
            if (!File.Exists(sysfsPath))
                return DiscType.Unknown;

            var content = await File.ReadAllTextAsync(sysfsPath, ct);
            if (!long.TryParse(content.Trim(), out var sectors))
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

    private async Task<string?> FindMountAsync(string devPath, CancellationToken ct)
    {
        var result = await runner.RunAsync("findmnt", $"--json {devPath}", timeoutMs: 10_000, ct: ct);
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StdOut))
            return null;

        using var doc = JsonDocument.Parse(result.StdOut);
        var filesystems = doc.RootElement.GetProperty("filesystems");
        foreach (var fs in filesystems.EnumerateArray())
        {
            var target = fs.GetProperty("target").GetString();
            if (target is not null && Directory.Exists(target))
                return target;
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
            var crc64 = await ComputeDvdCrc64Async(job.MountPoint!, ct);
            logger.LogInformation("DVD CRC64 hash is: {Crc64}", crc64);
            job.CrcId = crc64;

            var url = $"https://1337server.pythonanywhere.com/api/v1/?mode=s&crc64={crc64}";
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var response = await httpClient.GetStringAsync(url, ct);
            var armApiResult = JsonSerializer.Deserialize<ArmApiResponse>(response);

            if (armApiResult?.Success == true && armApiResult.Results?.Count > 0)
            {
                var first = armApiResult.Results["0"];
                logger.LogInformation("Found CRC64 id from online API: title={Title}", first.Title);
                job.Title = job.TitleAuto = first.Title;
                job.Year = job.YearAuto = first.Year;
                job.ImdbId = job.ImdbIdAuto = first.ImdbId;
                job.VideoType = job.VideoTypeAuto = first.VideoType;
                job.PosterUrl = job.PosterUrlAuto = first.PosterUrl;
                job.HasNiceTitle = true;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "DVD identification failed");
        }

        // Extract poster from disc while it's still mounted
        await SaveDiscPosterAsync(job, ct);

        return true;
    }

    private async Task SaveDiscPosterAsync(Job job, CancellationToken ct)
    {
        if (job.DiscType != DiscType.Dvd || string.IsNullOrEmpty(job.MountPoint))
            return;

        try
        {
            var typeSubFolder = ArmRipperService.ConvertJobType(job.VideoType);
            var jobTitle = ArmRipperService.FixJobTitle(job);
            var completedPath = settings.Value.CompletedPath ?? "/home/arm/media";
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
                    await runner.RunAsync("ffmpeg", $"-i \"{posterSrc}\" \"{posterDst}\"", timeoutMs: 30_000, ct: ct);
                    break;
                }
            }
        }
        catch { }
    }

    private Task<string> ComputeDvdCrc64Async(string mountPoint, CancellationToken ct)
    {
        return Task.Run(() => DvdCrc64.Compute(mountPoint), ct);
    }

    private async Task<bool> IdentifyBlurayAsync(Job job, CancellationToken ct)
    {
        var bdmtPath = Path.Combine(job.MountPoint!, "BDMV", "META", "DL", "bdmt_eng.xml");

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

            job.Title = job.TitleAuto = title;
            job.Year = job.YearAuto = year;

            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to parse bdmt_eng.xml for Bluray identification");

            if (!string.IsNullOrEmpty(job.Label))
            {
                var blurayTitle = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(
                    job.Label.Replace("_", " ").ToLowerInvariant());
                job.Title = job.TitleAuto = blurayTitle;
                job.Year = "";
                await db.SaveChangesAsync(ct);
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
        var title = job.Title;
        if (string.IsNullOrEmpty(title) || title == "not identified")
        {
            logger.LogInformation("Disc couldn't be identified");
            return;
        }

        var searchTitle = Regex.Replace(title.Trim(), "[_ ]", "+");
        var year = string.IsNullOrEmpty(job.Year) ? "" : Regex.Replace(job.Year, @"\D", "");

        logger.LogDebug("Calling webservice with title: {Title} and year: {Year}", searchTitle, year);
        await IdentifyLoopAsync(job, searchTitle, year, ct);
    }

    private async Task IdentifyLoopAsync(Job job, string title, string year, CancellationToken ct)
    {
        JsonDocument? response = null;

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

        while (response is null && title.Contains('-'))
        {
            title = title[..title.LastIndexOf('-')].TrimEnd('+');
            response = await CallMetadataProviderAsync(job, title, string.IsNullOrEmpty(year) ? null : year, ct);
        }

        while (response is null && title.Contains('+'))
        {
            title = title[..title.LastIndexOf('+')].TrimEnd('+');
            response = await CallMetadataProviderAsync(job, title, string.IsNullOrEmpty(year) ? null : year, ct);
            if (response is null)
                response = await CallMetadataProviderAsync(job, title, null, ct);
        }
    }

    private async Task<JsonDocument?> TryWithYearAsync(Job job, string title, string year, CancellationToken ct)
    {
        return await CallMetadataProviderAsync(job, title, year, ct);
    }

    private async Task<JsonDocument?> CallMetadataProviderAsync(Job job, string title, string? year, CancellationToken ct)
    {
        var provider = settings.Value.MetadataProvider?.ToLowerInvariant();
        return provider switch
        {
            "tmdb" => await TmdbSearchAsync(title, year, ct),
            "omdb" => await OmdbSearchAsync(title, year, ct),
            _ => null
        };
    }

    private async Task<JsonDocument?> OmdbSearchAsync(string title, string? year, CancellationToken ct)
    {
        var apiKey = settings.Value.OmdbApiKey;
        if (string.IsNullOrEmpty(apiKey))
            return null;

        var url = string.IsNullOrEmpty(year)
            ? $"https://www.omdbapi.com/?s={title}&r=json&apikey={apiKey}"
            : $"https://www.omdbapi.com/?s={title}&y={year}&r=json&apikey={apiKey}";

        try
        {
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
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

    private async Task<JsonDocument?> TmdbSearchAsync(string title, string? year, CancellationToken ct)
    {
        var apiKey = settings.Value.TmdbApiKey;
        if (string.IsNullOrEmpty(apiKey))
            return null;

        try
        {
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

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
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to compute disc fingerprint");
        }
    }

    private async Task UnmountAsync(Job job, CancellationToken ct)
    {
        try
        {
            await runner.RunAsync("umount", job.DevPath!, timeoutMs: 10_000, ct: ct);
            logger.LogInformation("Disc unmounted from {DevPath}", job.DevPath);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to unmount disc");
        }
    }

    private async Task EjectAsync(Job job, CancellationToken ct)
    {
        if (!settings.Value.AutoEject)
            return;

        try
        {
            await runner.RunAsync("umount", job.DevPath!, timeoutMs: 10_000, ct: ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to unmount disc");
        }

        try
        {
            await runner.RunAsync("eject", job.DevPath!, timeoutMs: 10_000, ct: ct);
            logger.LogInformation("Disc ejected from {DevPath}", job.DevPath);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to eject disc from {DevPath}", job.DevPath);
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
}
