using System.Diagnostics;
using System.Text.Json;
using ArmRipper.Core.Configuration;
using ArmRipper.Core.Infrastructure;
using ArmRipper.Core.Infrastructure.Data;
using ArmRipper.WebUi.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;

namespace ArmRipper.WebUi.Controllers;

[Authorize]
[Route("completed")]
public class CompletedController(IOptions<ArmSettings> settings, IMemoryCache cache, ArmDbContext db, IBackgroundRipService backgroundRip) : Controller
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);
    private const string CacheKey = "CompletedFiles";

    [HttpGet("")]
    public async Task<IActionResult> Index(bool refresh = false, CancellationToken ct = default)
    {
        if (refresh)
            cache.Remove(CacheKey);

        var files = await cache.GetOrCreateAsync(CacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheDuration;
            var cp = settings.Value.CompletedPath ?? "/home/arm/media";
            var rp = settings.Value.RawPath ?? "/home/arm/media/raw";
            var tp = settings.Value.TranscodePath ?? "/home/arm/media/transcode";

            // Scan the configured completed path.
            var completed = await ScanFilesAsync(cp, "Output", ct);
            // Also scan the default base path (/home/arm/media) so that files living
            // directly under movies/, tv/, etc. (legacy layout or mixed configs) are found.
            var basePath = "/home/arm/media";
            if (!string.Equals(cp, basePath, StringComparison.OrdinalIgnoreCase) && Directory.Exists(basePath))
            {
                var baseFiles = await ScanFilesAsync(basePath, "Output", ct);
                var existing = new HashSet<string>(completed.Select(f => f.FilePath), StringComparer.OrdinalIgnoreCase);
                completed.AddRange(baseFiles.Where(f => !existing.Contains(f.FilePath)));
            }
            var raw = await ScanFilesAsync(rp, "Raw", ct);
            var transcode = await ScanFilesAsync(tp, "Transcode", ct);

            // Raw/Transcode paths may be subdirectories of the completed/base path.
            // Deduplicate: prefer the more specific source (Raw > Transcode > Output).
            var all = completed
                .Concat(transcode)
                .Concat(raw)
                .ToList();
            var deduped = new List<CompletedFileInfo>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in all.OrderBy(f => f.Source == "Raw" ? 0 : f.Source == "Transcode" ? 1 : 2))
            {
                if (seen.Add(f.FilePath))
                    deduped.Add(f);
            }
            return deduped;
        });

        return View(files);
    }

    [HttpGet("probe")]
    public async Task<IActionResult> Probe(string filePath, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(filePath) || !System.IO.File.Exists(filePath))
            return NotFound();

        var (basePath, source) = ResolveSource(filePath);
        var info = await ProbeFileAsync(filePath, basePath, source, ct);
        if (info == null)
            return NotFound();

        return View("Detail", info);
    }

    [HttpPost("delete")]
    public IActionResult Delete(string filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !System.IO.File.Exists(filePath))
        {
            TempData["ErrorMessage"] = "File not found.";
            return RedirectToAction("Index");
        }

        try
        {
            System.IO.File.Delete(filePath);
            cache.Remove(CacheKey);
            TempData["SuccessMessage"] = $"Deleted {Path.GetFileName(filePath)}";
        }
        catch (Exception ex)
        {
            TempData["ErrorMessage"] = $"Failed to delete: {ex.Message}";
        }

        return RedirectToAction("Index");
    }

    [HttpPost("rename")]
    public IActionResult Rename(string filePath, string newName)
    {
        if (string.IsNullOrEmpty(filePath) || !System.IO.File.Exists(filePath))
        {
            TempData["ErrorMessage"] = "File not found.";
            return RedirectToAction("Index");
        }

        if (string.IsNullOrWhiteSpace(newName))
        {
            TempData["ErrorMessage"] = "New name cannot be empty.";
            return RedirectToAction("Index");
        }

        // Strip any path separators — we only rename within the same directory
        var safeName = newName.Replace('/', '_').Replace('\\', '_').Trim();
        var dir = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(dir))
        {
            TempData["ErrorMessage"] = "Cannot determine parent directory.";
            return RedirectToAction("Index");
        }

        var targetPath = Path.Combine(dir, safeName);

        if (string.Equals(filePath, targetPath, StringComparison.OrdinalIgnoreCase))
        {
            TempData["SuccessMessage"] = "Name unchanged.";
            return RedirectToAction("Index");
        }

        if (System.IO.File.Exists(targetPath))
        {
            TempData["ErrorMessage"] = $"A file named \"{safeName}\" already exists in that directory.";
            return RedirectToAction("Index");
        }

        try
        {
            System.IO.File.Move(filePath, targetPath);
            cache.Remove(CacheKey);
            TempData["SuccessMessage"] = $"Renamed {Path.GetFileName(filePath)} → {safeName}";
        }
        catch (Exception ex)
        {
            TempData["ErrorMessage"] = $"Failed to rename: {ex.Message}";
        }

        return RedirectToAction("Index");
    }

    [HttpPost("transcode")]
    public async Task<IActionResult> Transcode(string filePath, int? originalJobId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(filePath) || !System.IO.File.Exists(filePath))
        {
            TempData["ErrorMessage"] = "Raw file not found.";
            return RedirectToAction("Index");
        }

        // If no job ID was provided, try to find one by directory name
        var jobId = originalJobId;
        if (jobId is null or 0)
        {
            var dirName = Path.GetFileName(Path.GetDirectoryName(filePath));
            if (!string.IsNullOrEmpty(dirName))
            {
                // Try to parse "Title (Year)" format
                var match = System.Text.RegularExpressions.Regex.Match(dirName, @"^(.+?) \((\d{4})\)$");
                if (match.Success)
                {
                    var title = match.Groups[1].Value;
                    var year = match.Groups[2].Value;
                    var found = await db.Jobs
                        .Where(j => j.Title == title && j.Year == year)
                        .OrderByDescending(j => j.Id)
                        .FirstOrDefaultAsync(ct);
                    if (found is not null)
                        jobId = found.Id;
                }

                if (jobId is null or 0)
                {
                    // Fallback: try matching just the title
                    var found = await db.Jobs
                        .Where(j => j.Title == dirName || j.TitleManual == dirName)
                        .OrderByDescending(j => j.Id)
                        .FirstOrDefaultAsync(ct);
                    if (found is not null)
                        jobId = found.Id;
                }
            }
        }

        if (jobId is null or 0)
        {
            TempData["ErrorMessage"] = "Could not find the original job for this file. Ensure it was ripped through ARM first.";
            return RedirectToAction("Index");
        }

        backgroundRip.StartForkedJob(jobId.Value, filePath, ct);
        cache.Remove(CacheKey);

        TempData["SuccessMessage"] = $"Forked transcode job started for {Path.GetFileName(filePath)} (original job #{jobId})";
        return RedirectToAction("Index");
    }

    private (string basePath, string source) ResolveSource(string filePath)
    {
        var completed = (settings.Value.CompletedPath ?? "/home/arm/media").TrimEnd('/');
        var raw = (settings.Value.RawPath ?? "/home/arm/media/raw").TrimEnd('/');
        var transcode = (settings.Value.TranscodePath ?? "/home/arm/media/transcode").TrimEnd('/');

        if (filePath.StartsWith(raw, StringComparison.OrdinalIgnoreCase))
            return (raw, "Raw");
        if (filePath.StartsWith(transcode, StringComparison.OrdinalIgnoreCase))
            return (transcode, "Transcode");
        return (completed, "Output");
    }

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".m4v", ".avi", ".mov", ".webm", ".ts", ".m2ts", ".iso", ".mpeg", ".mpg", ".wmv", ".flv", ".ogv"
    };

    private async Task<List<CompletedFileInfo>> ScanFilesAsync(string basePath, string source, CancellationToken ct)
    {
        if (!Directory.Exists(basePath))
            return [];

        var files = Directory.EnumerateFiles(basePath, "*.*", SearchOption.AllDirectories)
            .Where(f => VideoExtensions.Contains(Path.GetExtension(f)))
            .ToList();

        var tasks = files.Select(file => ProbeFileAsync(file, basePath, source, ct));
        var results = await Task.WhenAll(tasks);

        // Keep all results — ProbeFileAsync now always returns an info object
        // (with ffprobe data if available, or basic filesystem info as fallback).
        var list = results
            .OrderByDescending(r => r.LastModified)
            .ToList();

        // Try to look up the producing job for ALL files by examining the directory name.
        // For Raw files we also do a precise track-level match.
        foreach (var info in list)
        {
            var dirPath = Path.GetDirectoryName(info.FilePath);
            if (string.IsNullOrEmpty(dirPath)) continue;
            var dirName = Path.GetFileName(dirPath);
            var fileName = Path.GetFileName(info.FilePath);
            if (string.IsNullOrEmpty(dirName)) continue;

            // ── Attempt #1: Track-level match (most precise) ──
            // Works for Raw files where the file name matches a Track record.
            if (source == "Raw" && !string.IsNullOrEmpty(fileName))
            {
                var trackJobId = await db.Tracks
                    .Where(t => t.BaseName == dirName && t.FileName == fileName)
                    .Select(t => t.JobId)
                    .FirstOrDefaultAsync(ct);
                if (trackJobId != 0)
                {
                    var job = await db.Jobs.FindAsync(new object[] { trackJobId }, ct);
                    info.JobId = trackJobId;
                    info.OriginalJobId = trackJobId;
                    info.JobTitle = job?.Title;
                    continue;
                }
            }

            // ── Attempt #2: Match by Job.Path ──
            // Works for Output/Transcode files where job.Path matches the file's parent dir.
            var jobByPath = await db.Jobs
                .Where(j => j.Path != null && j.Path == dirPath)
                .OrderByDescending(j => j.Id)
                .FirstOrDefaultAsync(ct);
            if (jobByPath is not null)
            {
                info.JobId = jobByPath.Id;
                info.JobTitle = jobByPath.Title;
                if (source == "Raw")
                    info.OriginalJobId = jobByPath.Id;
                continue;
            }

            // ── Attempt #3: Directory name is "Title (Year)" ──
            var match = System.Text.RegularExpressions.Regex.Match(dirName, @"^(.+?) \((\d{4})\)$");
            if (match.Success)
            {
                var title = match.Groups[1].Value;
                var year = match.Groups[2].Value;
                var foundByTitleYear = await db.Jobs
                    .Where(j => j.Title == title && j.Year == year)
                    .OrderByDescending(j => j.Id)
                    .FirstOrDefaultAsync(ct);
                if (foundByTitleYear is not null)
                {
                    info.JobId = foundByTitleYear.Id;
                    if (source == "Raw")
                        info.OriginalJobId = foundByTitleYear.Id;
                    info.JobTitle = foundByTitleYear.Title;
                    continue;
                }
            }

            // ── Attempt #4: Match directory name as Job.Title or TitleManual ──
            var fallback = await db.Jobs
                .Where(j => j.Title == dirName || j.TitleManual == dirName || j.Path != null && j.Path.EndsWith(dirName))
                .OrderByDescending(j => j.Id)
                .FirstOrDefaultAsync(ct);
            if (fallback is not null)
            {
                info.JobId = fallback.Id;
                if (source == "Raw")
                    info.OriginalJobId = fallback.Id;
                info.JobTitle = fallback.Title;
            }
        }

        return list;
    }

    /// <summary>
    /// Probes a media file with ffprobe and returns a <see cref="CompletedFileInfo"/>.
    /// If ffprobe fails or is unavailable, returns an info object populated with basic
    /// filesystem metadata (file name, size, last-modified) so the file is still visible.
    /// </summary>
    private async Task<CompletedFileInfo> ProbeFileAsync(string filePath, string basePath, string source, CancellationToken ct)
    {
        // Start with a basic info object from filesystem data (always succeeds).
        var fileInfo = new System.IO.FileInfo(filePath);
        var info = new CompletedFileInfo
        {
            FilePath = filePath,
            RelativeDirectory = GetRelativeDirectory(filePath, basePath),
            Source = source,
            LastModified = fileInfo.LastWriteTimeUtc,
            SizeBytes = fileInfo.Length,
            DurationSeconds = 0,
            BitrateKbps = 0
        };

        try
        {
            ct.ThrowIfCancellationRequested();

            var psi = new ProcessStartInfo
            {
                FileName = "ffprobe",
                Arguments = $"-v quiet -print_format json -show_format -show_streams \"{filePath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = psi };
            process.Start();
            var json = await process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            if (process.ExitCode != 0)
                return info; // fallback — basic info only

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var format = root.GetProperty("format");
            if (format.TryGetProperty("size", out var sizeEl) && long.TryParse(sizeEl.GetString(), out var s))
                info.SizeBytes = s;
            if (format.TryGetProperty("duration", out var durEl) && double.TryParse(durEl.GetString(), out var d))
                info.DurationSeconds = d;
            if (format.TryGetProperty("bit_rate", out var brEl) && long.TryParse(brEl.GetString(), out var b))
                info.BitrateKbps = b / 1000.0;

            var streams = root.GetProperty("streams").EnumerateArray().ToList();
            foreach (var stream in streams)
            {
                var codecType = stream.GetProperty("codec_type").GetString();
                switch (codecType)
                {
                    case "video":
                        info.Video = ParseVideoStream(stream);
                        break;
                    case "audio":
                        info.AudioStreams.Add(ParseAudioStream(stream));
                        break;
                    case "subtitle":
                        info.SubtitleStreams.Add(ParseSubtitleStream(stream));
                        break;
                }
            }
        }
        catch
        {
            // ffprobe failed — return the fallback info we already built
        }

        return info;
    }

    private static VideoStreamInfo ParseVideoStream(JsonElement stream)
    {
        return new VideoStreamInfo
        {
            CodecName = stream.GetProperty("codec_name").GetString() ?? "",
            CodecLongName = stream.GetProperty("codec_long_name").GetString() ?? "",
            Profile = stream.TryGetProperty("profile", out var p) ? p.GetString() ?? "" : "",
            Width = stream.TryGetProperty("width", out var w) ? w.GetInt32() : 0,
            Height = stream.TryGetProperty("height", out var h) ? h.GetInt32() : 0,
            PixelFormat = stream.TryGetProperty("pix_fmt", out var pf) ? pf.GetString() ?? "" : "",
            FrameRate = stream.TryGetProperty("avg_frame_rate", out var fr) ? fr.GetString() ?? "" : "",
            ColorSpace = stream.TryGetProperty("color_space", out var cs) ? cs.GetString() ?? "" : "",
            ColorTransfer = stream.TryGetProperty("color_transfer", out var ct) ? ct.GetString() ?? "" : "",
            BFrames = stream.TryGetProperty("has_b_frames", out var bf) ? bf.GetInt32() : null
        };
    }

    private static AudioStreamInfo ParseAudioStream(JsonElement stream)
    {
        return new AudioStreamInfo
        {
            CodecName = stream.GetProperty("codec_name").GetString() ?? "",
            CodecLongName = stream.GetProperty("codec_long_name").GetString() ?? "",
            Channels = stream.TryGetProperty("channels", out var ch) ? ch.GetInt32() : 0,
            ChannelLayout = stream.TryGetProperty("channel_layout", out var cl) ? cl.GetString() ?? "" : "",
            SampleRate = stream.TryGetProperty("sample_rate", out var sr) && int.TryParse(sr.GetString(), out var rate) ? rate : 0,
            Bitrate = stream.TryGetProperty("bit_rate", out var br) && int.TryParse(br.GetString(), out var bitr) ? bitr : 0,
            Language = (stream.TryGetProperty("tags", out var tags) && tags.TryGetProperty("language", out var lang)) ? lang.GetString() ?? "" : "",
            Title = (stream.TryGetProperty("tags", out var tags2) && tags2.TryGetProperty("title", out var t)) ? t.GetString() : null
        };
    }

    private static SubtitleStreamInfo ParseSubtitleStream(JsonElement stream)
    {
        return new SubtitleStreamInfo
        {
            CodecName = stream.GetProperty("codec_name").GetString() ?? "",
            Language = (stream.TryGetProperty("tags", out var tags) && tags.TryGetProperty("language", out var lang)) ? lang.GetString() ?? "" : "",
            Forced = stream.TryGetProperty("disposition", out var disp) && disp.TryGetProperty("forced", out var f) && f.GetInt32() == 1
        };
    }

    private string GetRelativeDirectory(string filePath, string basePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (dir == null) return "";
        return dir.StartsWith(basePath, StringComparison.OrdinalIgnoreCase)
            ? dir[basePath.Length..].TrimStart(Path.DirectorySeparatorChar)
            : "";
    }
}
