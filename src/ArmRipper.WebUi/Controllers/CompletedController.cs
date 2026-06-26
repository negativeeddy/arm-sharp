using System.Diagnostics;
using System.Text.Json;
using ArmRipper.Core.Configuration;
using ArmRipper.WebUi.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace ArmRipper.WebUi.Controllers;

[Authorize]
[Route("completed")]
public class CompletedController(IOptions<ArmSettings> settings, IMemoryCache cache) : Controller
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
            var completed = await ScanFilesAsync(settings.Value.CompletedPath ?? "/home/arm/media", "Output", ct);
            var raw = await ScanFilesAsync(settings.Value.RawPath ?? "/home/arm/media/raw", "Raw", ct);
            var transcode = await ScanFilesAsync(settings.Value.TranscodePath ?? "/home/arm/media/transcode", "Transcode", ct);
            return completed.Concat(raw).Concat(transcode).ToList();
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

    private async Task<List<CompletedFileInfo>> ScanFilesAsync(string basePath, string source, CancellationToken ct)
    {
        if (!Directory.Exists(basePath))
            return [];

        var videoExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".mkv", ".mp4", ".m4v", ".avi", ".mov" };

        var files = Directory.EnumerateFiles(basePath, "*.*", SearchOption.AllDirectories)
            .Where(f => videoExtensions.Contains(Path.GetExtension(f)))
            .ToList();

        var tasks = files.Select(file => ProbeFileAsync(file, basePath, source, ct));
        var results = await Task.WhenAll(tasks);

        return results.Where(r => r != null)
            .OrderByDescending(r => r!.LastModified)
            .ToList()!;
    }

    private async Task<CompletedFileInfo?> ProbeFileAsync(string filePath, string basePath, string source, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();

            var startInfo = new ProcessStartInfo
            {
                FileName = "ffprobe",
                Arguments = $"-v quiet -print_format json -show_format -show_streams \"{filePath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = startInfo };
            process.Start();
            var json = await process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            if (process.ExitCode != 0)
                return null;

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var format = root.GetProperty("format");
            var sizeBytes = format.TryGetProperty("size", out var sizeEl) && long.TryParse(sizeEl.GetString(), out var s) ? s : 0L;
            var duration = format.TryGetProperty("duration", out var durEl) && double.TryParse(durEl.GetString(), out var d) ? d : 0.0;
            var bitrate = format.TryGetProperty("bit_rate", out var brEl) && long.TryParse(brEl.GetString(), out var b) ? b : 0L;

            var info = new CompletedFileInfo
            {
                FilePath = filePath,
                RelativeDirectory = GetRelativeDirectory(filePath, basePath),
                Source = source,
                LastModified = System.IO.File.GetLastWriteTimeUtc(filePath),
                SizeBytes = sizeBytes,
                DurationSeconds = duration,
                BitrateKbps = bitrate / 1000.0
            };

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

            return info;
        }
        catch
        {
            return null;
        }
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
