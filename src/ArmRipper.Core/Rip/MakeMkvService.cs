using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using ArmRipper.Core.Configuration;
using ArmRipper.Core.Infrastructure;
using ArmRipper.Core.Infrastructure.Data;
using ArmRipper.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArmRipper.Core.Rip;

public partial class MakeMkvService
{
    private const int UnknownDrv = 999;
    private const int MaxDevices = 16;
    private const string Source = "MakeMKV";
    private const string BetaKeyApi = "https://cable.ayra.ch/MakeMKV/api.php?json";
    private const string BetaKeyForum = "https://forum.makemkv.com/forum/viewtopic.php?f=5&t=1053";
    internal static string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".MakeMKV", "settings.conf");

    private readonly ICliProcessRunner _runner;
    private readonly ILogger<MakeMkvService> _logger;
    private readonly IOptions<ArmSettings> _settings;
    private readonly ArmDbContext _db;

    public MakeMkvService(ICliProcessRunner runner, ILogger<MakeMkvService> logger, IOptions<ArmSettings> settings, ArmDbContext db)
    {
        _runner = runner;
        _logger = logger;
        _settings = settings;
        _db = db;
    }

    public async Task EnsureKeyAsync(CancellationToken ct = default)
    {
        var configuredKey = _settings.Value.MakeMkvPermaKey;
        if (!string.IsNullOrEmpty(configuredKey))
        {
            await RegisterKeyAsync(configuredKey, ct);
            return;
        }

        var key = await FetchBetaKeyAsync(ct);
        if (!string.IsNullOrEmpty(key))
        {
            await RegisterKeyAsync(key, ct);
            _logger.LogInformation("Auto-updated MakeMKV beta key");
        }
    }

    private static async Task<string?> FetchBetaKeyAsync(CancellationToken ct)
    {
        try
        {
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

            // Primary: Ayra JSON API
            try
            {
                var json = await httpClient.GetStringAsync(BetaKeyApi, ct);
                var doc = System.Text.Json.JsonDocument.Parse(json);
                var key = doc.RootElement.GetProperty("key").GetString();
                if (!string.IsNullOrEmpty(key) && key.StartsWith("T-"))
                    return key;
            }
            catch { }

            // Fallback: scrape the MakeMKV forum
            var html = await httpClient.GetStringAsync(BetaKeyForum, ct);
            var match = Regex.Match(html, @"<code>(T-[A-Za-z0-9_@]+)</code>");
            if (match.Success)
                return match.Groups[1].Value;
        }
        catch { }

        return null;
    }

    private async Task RegisterKeyAsync(string key, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(SettingsPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        await File.WriteAllTextAsync(SettingsPath, $"app_Key = \"{key}\"\n", ct);
        _logger.LogInformation("MakeMKV key saved to {Path}", SettingsPath);
    }

    public async IAsyncEnumerable<T> RunAsync<T>(
        string[] options,
        MakeMkvOutputType select,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await EnsureKeyAsync(ct);

        var cmd = new[] { "makemkvcon", "--robot", "--messages=-stdout" }.Concat(options).ToArray();

        await foreach (var line in _runner.RunStreamingAsync(cmd[0], string.Join(" ", cmd[1..]), ct: ct))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            var parsed = ParseLine(line);
            if (parsed is null) continue;

            if (select.HasFlag(parsed.Type) && parsed.Data is T result)
                yield return result;
        }
    }

    public async Task<List<Track>> GetTrackInfoAsync(Job job, string baseName, CancellationToken ct = default)
    {
        await EnsureKeyAsync(ct);

        var tracks = new List<Track>();
        var discTracks = new List<DiscTrack>();
        var minLength = job.Config?.MinLength ?? _settings.Value.MinLength;

        var fileName = "makemkvcon";
        var arguments = $"--robot --messages=-stdout info dev:{job.DevPath} --minlength={minLength}";

        var result = await _runner.RunAsync(fileName, arguments, timeoutMs: 300_000, ct: ct);
        if (result.TimedOut)
        {
            _logger.LogWarning("makemkvcon info timed out");
            return tracks;
        }

        var currentTid = -1;
        var seconds = 0;
        var aspect = "";
        var fps = 0.0;
        var filename = "";
        var chapters = 0;
        var filesize = 0L;
        var streamType = 0;
        var resolution = "";
        var streamAccums = new Dictionary<int, StreamAccum>();

        var lineCount = 0;
        using var reader = new StringReader(result.StdOut);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            lineCount++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parsed = ParseLine(line);
            if (parsed is null) continue;

            switch (parsed.Data)
            {
                case SinFo sinfo:
                    switch ((StreamId)sinfo.Id)
                    {
                        case StreamId.Type:
                            streamType = sinfo.Code;
                            GetOrCreateAccum(streamAccums, sinfo.Sid).StreamTypeCode = sinfo.Code;
                            break;
                        case StreamId.LanguageCode:
                            GetOrCreateAccum(streamAccums, sinfo.Sid).LanguageCode = sinfo.Value.Trim();
                            break;
                        case StreamId.CodecId:
                            GetOrCreateAccum(streamAccums, sinfo.Sid).Codec = sinfo.Value.Trim();
                            break;
                        case StreamId.Channels:
                            if (int.TryParse(sinfo.Value.Trim(), out var ch))
                                GetOrCreateAccum(streamAccums, sinfo.Sid).Channels = ch;
                            break;
                        case StreamId.Forced:
                            GetOrCreateAccum(streamAccums, sinfo.Sid).Forced = sinfo.Value.Trim() is "1";
                            break;
                        case StreamId.Resolution:
                            resolution = sinfo.Value.Trim();
                            break;
                        case StreamId.Aspect:
                            if (streamType == MakeMkvStreamCodes.Video)
                                aspect = sinfo.Value.Trim();
                            break;
                        case StreamId.Fps:
                            if (streamType == MakeMkvStreamCodes.Video)
                            {
                                var fpsParts = sinfo.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                                if (fpsParts.Length > 0)
                                    double.TryParse(fpsParts[0], out fps);
                            }
                            break;
                    }
                    break;

                case TInfo tinfo:
                    if (currentTid >= 0 && tinfo.Tid != currentTid)
                        FinalizeTrack(job, baseName, tracks, discTracks, currentTid, ref seconds, ref aspect, ref fps, ref filename, ref chapters, ref filesize, ref streamType, ref resolution, streamAccums);
                    currentTid = tinfo.Tid;
                    switch ((TrackId)tinfo.Id)
                    {
                        case TrackId.Filename:
                            filename = StripQuotes(tinfo.Value);
                            break;
                        case TrackId.Duration:
                            seconds = HmsToSeconds(tinfo.Value);
                            break;
                        case TrackId.Chapters:
                            int.TryParse(tinfo.Value, out chapters);
                            break;
                        case TrackId.Filesize:
                            long.TryParse(tinfo.Value, out filesize);
                            break;
                    }
                    break;

                case Titles titles:
                    _logger.LogInformation("Found {Count} titles on disc", titles.Count);
                    break;
            }
        }

        if (currentTid >= 0)
            FinalizeTrack(job, baseName, tracks, discTracks, currentTid, ref seconds, ref aspect, ref fps, ref filename, ref chapters, ref filesize, ref streamType, ref resolution, streamAccums);

        _logger.LogInformation("GetTrackInfo: {Lines} lines, {Tracks} tracks, lastTid={Tid}", lineCount, tracks.Count, currentTid);

        await PersistTrackCacheAsync(job, discTracks, ct);

        return tracks;
    }

    public async Task<List<Track>> GetTrackInfoWithCacheAsync(Job job, string baseName, CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(job.DiscFingerprint))
        {
            var cached = await _db.DiscMetadata
                .AsNoTracking()
                .Include(d => d.Tracks)
                .ThenInclude(t => t.Streams)
                .FirstOrDefaultAsync(d => d.Fingerprint == job.DiscFingerprint, ct);

            if (cached is not null)
            {
                _logger.LogInformation("Found cached track info for fingerprint {Fingerprint} ({Count} tracks)",
                    job.DiscFingerprint, cached.Tracks.Count);

                cached.LastUsedAt = DateTime.UtcNow;
                _db.DiscMetadata.Update(cached);
                await _db.SaveChangesAsync(ct);

                var tracks = cached.Tracks.Select(t => new Track
                {
                    JobId = job.Id,
                    TrackNumber = t.TrackNumber,
                    FileName = t.FileName,
                    Length = t.Length,
                    AspectRatio = t.AspectRatio,
                    Fps = t.Fps,
                    Chapters = t.Chapters,
                    FileSize = t.FileSize,
                    Source = Source,
                    BaseName = baseName,
                }).ToList();

                return tracks;
            }
        }

        return await GetTrackInfoAsync(job, baseName, ct);
    }

    private static StreamAccum GetOrCreateAccum(Dictionary<int, StreamAccum> accums, int sid)
    {
        if (!accums.TryGetValue(sid, out var accum))
        {
            accum = new StreamAccum();
            accums[sid] = accum;
        }
        return accum;
    }

    private static void FinalizeTrack(Job job, string baseName, List<Track> tracks, List<DiscTrack> discTracks,
        int currentTid, ref int seconds, ref string aspect, ref double fps, ref string filename,
        ref int chapters, ref long filesize, ref int streamType, ref string resolution,
        Dictionary<int, StreamAccum> streamAccums)
    {
        tracks.Add(CreateTrackObj(job, currentTid, baseName, seconds, aspect, fps, filename, chapters, filesize));

        var discTrack = new DiscTrack
        {
            TrackNumber = currentTid.ToString(),
            FileName = string.IsNullOrEmpty(filename) ? null : filename,
            Length = seconds > 0 ? seconds : null,
            Chapters = chapters > 0 ? chapters : null,
            FileSize = filesize > 0 ? filesize : null,
            AspectRatio = string.IsNullOrEmpty(aspect) ? null : aspect,
            Fps = fps > 0 ? fps : null,
            Resolution = string.IsNullOrEmpty(resolution) ? null : resolution,
        };

        foreach (var (sid, accum) in streamAccums)
        {
            var typeName = accum.StreamTypeCode switch
            {
                MakeMkvStreamCodes.Video => "Video",
                MakeMkvStreamCodes.Audio => "Audio",
                MakeMkvStreamCodes.Subtitle => "Subtitle",
                _ => "Unknown"
            };

            discTrack.Streams.Add(new DiscTrackStream
            {
                StreamIndex = sid,
                StreamType = typeName,
                LanguageCode = accum.LanguageCode,
                Codec = accum.Codec,
                ChannelCount = accum.Channels,
                Forced = accum.Forced,
            });
        }

        discTracks.Add(discTrack);

        seconds = 0; aspect = ""; fps = 0.0; filename = ""; chapters = 0; filesize = 0; streamType = 0; resolution = "";
        streamAccums.Clear();
    }

    private async Task PersistTrackCacheAsync(Job job, List<DiscTrack> discTracks, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(job.DiscFingerprint) || discTracks.Count == 0)
            return;

        try
        {
            var existing = await _db.DiscMetadata
                .FirstOrDefaultAsync(d => d.Fingerprint == job.DiscFingerprint, ct);

            if (existing is not null)
            {
                existing.LastUsedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync(ct);
                _logger.LogInformation("Disc fingerprint {Fingerprint} already cached, updating timestamp",
                    job.DiscFingerprint);
                return;
            }
            else
            {
                var metadata = new DiscMetadata
                {
                    Fingerprint = job.DiscFingerprint,
                    VolumeLabel = job.Label ?? "",
                    SectorCount = 0,
                    DiscType = job.DiscType.ToString(),
                    CreatedAt = DateTime.UtcNow,
                    LastUsedAt = DateTime.UtcNow,
                    Tracks = discTracks,
                };

                _db.DiscMetadata.Add(metadata);
                _logger.LogInformation("Caching {Count} tracks for disc fingerprint {Fingerprint}",
                    discTracks.Count, job.DiscFingerprint);
            }

            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist disc track cache for fingerprint {Fingerprint}", job.DiscFingerprint);
        }
    }

    private sealed record StreamAccum
    {
        public int StreamTypeCode { get; set; }
        public string? LanguageCode { get; set; }
        public string? Codec { get; set; }
        public int? Channels { get; set; }
        public bool Forced { get; set; }
    }

    public async Task RipTrackAsync(Job job, string trackNumber, string outputPath, string mkvArgs, int minLength, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        // Estimate expected file size from track info for progress monitoring
        var expectedSize = job.Tracks
            .Where(t => t.TrackNumber == trackNumber)
            .Sum(t => t.FileSize ?? 0);

        var monitorCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var monitorTask = expectedSize > 0 && progress is not null
            ? MonitorRipFileSizeAsync(outputPath, expectedSize, progress, monitorCts.Token)
            : Task.CompletedTask;

        try
        {
            var args = $"--robot --messages=-stdout --progress=-stdout mkv --minlength={minLength} dev:{job.DevPath} {trackNumber} \"{outputPath}\"";
            if (!string.IsNullOrEmpty(mkvArgs))
                args = $"--robot --messages=-stdout --progress=-stdout mkv {mkvArgs} --minlength={minLength} dev:{job.DevPath} {trackNumber} \"{outputPath}\"";

            await foreach (var line in _runner.RunStreamingAsync("makemkvcon", args, ct: ct))
                ParseAndReportProgress(line, progress);

            // Rip completed successfully — report 100%
            if (progress is not null)
                progress.Report(100);
        }
        finally
        {
            // Stop the file-size monitor
            monitorCts.Cancel();
            try { await monitorTask; } catch (OperationCanceledException) { }
        }
    }

    public async Task RipAllTitlesAsync(Job job, string outputPath, string mkvArgs, int minLength, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        // Estimate total expected size from all eligible tracks
        var minLen = job.Config?.MinLength ?? _settings.Value.MinLength;
        var maxLen = job.Config?.MaxLength ?? _settings.Value.MaxLength;
        var expectedSize = job.Tracks
            .Where(t => (t.Length ?? 0) >= minLen && (t.Length ?? 0) <= maxLen)
            .Sum(t => t.FileSize ?? 0);

        var monitorCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var monitorTask = expectedSize > 0 && progress is not null
            ? MonitorRipFileSizeAsync(outputPath, expectedSize, progress, monitorCts.Token)
            : Task.CompletedTask;

        try
        {
            var args = $"--robot --messages=-stdout --progress=-stdout mkv --minlength={minLength} dev:{job.DevPath} all \"{outputPath}\"";
            if (!string.IsNullOrEmpty(mkvArgs))
                args = $"--robot --messages=-stdout --progress=-stdout mkv {mkvArgs} --minlength={minLength} dev:{job.DevPath} all \"{outputPath}\"";

            await foreach (var line in _runner.RunStreamingAsync("makemkvcon", args, ct: ct))
                ParseAndReportProgress(line, progress);

            // Rip completed successfully — report 100%
            if (progress is not null)
                progress.Report(100);
        }
        finally
        {
            // Stop the file-size monitor
            monitorCts.Cancel();
            try { await monitorTask; } catch (OperationCanceledException) { }
        }
    }

    /// <summary>
    /// Monitors the output directory for .mkv file growth and reports progress
    /// based on total written bytes vs expected size. Acts as a fallback when
    /// MakeMKV does not emit PRGC/PRGV progress lines during the rip phase.
    /// Only counts .mkv files created after this monitor starts, so that
    /// sequential per-track rips don't double-count previous tracks' files.
    /// </summary>
    private async Task MonitorRipFileSizeAsync(string outputPath, long expectedSize, IProgress<int> progress, CancellationToken ct)
    {
        if (expectedSize <= 0) return;

        // Snapshot files that already exist before this rip starts,
        // so we only count newly created files during this rip session.
        var preExisting = Directory.Exists(outputPath)
            ? Directory.EnumerateFiles(outputPath, "*.mkv").ToHashSet()
            : new HashSet<string>();

        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(2000, ct);

                if (!Directory.Exists(outputPath)) continue;

                long totalSize = 0;
                try
                {
                    foreach (var file in Directory.EnumerateFiles(outputPath, "*.mkv"))
                    {
                        // Skip files that were already there before this rip started
                        if (preExisting.Contains(file)) continue;
                        totalSize += new FileInfo(file).Length;
                    }
                }
                catch (UnauthorizedAccessException) { continue; }
                catch (DirectoryNotFoundException) { continue; }

                if (totalSize > 0)
                {
                    var pct = (int)(totalSize * 100.0 / expectedSize);
                    pct = Math.Clamp(pct, 0, 99); // Cap at 99%; 100% is set by the caller on completion
                    progress.Report(pct);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error in MakeMKV file-size progress monitor");
        }
    }

    private void ParseAndReportProgress(string line, IProgress<int>? progress)
    {
        if (progress is null) return;

        try
        {
            var parsed = ParseLine(line);
            if (parsed is null) return;

            var pct = parsed.Type switch
            {
                MakeMkvOutputType.PrgC when parsed.Data is PrgC pc => pc.TotalProgress > 0 ? (int)(pc.CurrentProgress * 100.0 / pc.TotalProgress) : (int?)null,
                MakeMkvOutputType.PrgV when parsed.Data is PrgV pv => pv.TotalProgress > 0 ? (int)(pv.CurrentProgress * 100.0 / pv.TotalProgress) : (int?)null,
                _ => (int?)null
            };

            if (pct.HasValue)
                progress.Report(pct.Value);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error parsing MakeMKV progress line: {Line}", line);
        }
    }

    private static Track CreateTrackObj(Job job, int tid, string baseName, int seconds, string aspect, double fps, string filename, int chapters, long filesize)
    {
        return new Track
        {
            JobId = job.Id,
            TrackNumber = tid.ToString(),
            Length = seconds,
            AspectRatio = string.IsNullOrEmpty(aspect) ? null : aspect,
            Fps = fps > 0 ? fps : null,
            FileName = string.IsNullOrEmpty(filename) ? null : filename,
            Chapters = chapters > 0 ? chapters : null,
            FileSize = filesize > 0 ? filesize : null,
            Source = Source,
            BaseName = baseName,
        };
    }

    private static string[] SplitArgs(string args)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < args.Length; i++)
        {
            var c = args[i];
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (c == ' ' && !inQuotes)
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
            result.Add(current.ToString());

        return [.. result];
    }

    private static string StripQuotes(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            return value[1..^1];
        return value;
    }

    public record ParsedLine(MakeMkvOutputType Type, object Data);

    public ParsedLine? ParseLine(string line)
    {
        var colonIdx = line.IndexOf(':');
        if (colonIdx < 0) return null;

        var typeStr = line[..colonIdx];
        var content = line[(colonIdx + 1)..];

        if (!Enum.TryParse<MakeMkvOutputType>(typeStr, ignoreCase: true, out var type))
            return null;

        return type switch
        {
            MakeMkvOutputType.Msg => ParseMsg(content),
            MakeMkvOutputType.Drv => ParseDrv(content),
            MakeMkvOutputType.TCount => new ParsedLine(type, new Titles(int.Parse(content))),
            MakeMkvOutputType.CinFo => ParseCInfo(content),
            MakeMkvOutputType.TInfo => ParseTInfo(content),
            MakeMkvOutputType.SinFo => ParseSInfo(content),
            MakeMkvOutputType.PrgV => ParsePrgV(content),
            MakeMkvOutputType.PrgC => ParsePrgC(content),
            MakeMkvOutputType.PrgT => ParsePrgT(content),
            _ => null
        };
    }

    private ParsedLine ParseMsg(string content)
    {
        var parts = SplitCsv(content);
        var code = int.Parse(parts[0]);
        var flags = int.Parse(parts[1]);
        var count = int.Parse(parts[2]);
        var message = parts[3];
        var sprintf = parts.Length > 4 ? parts[4] : "";
        var @params = parts.Length > 5 ? parts[5..] : [];

        return new ParsedLine(MakeMkvOutputType.Msg,
            new MakeMkvMessage(code, flags, count, message, sprintf, @params));
    }

    private ParsedLine ParseDrv(string content)
    {
        var parts = SplitCsv(content);
        var index = int.Parse(parts[0]);
        var visible = int.Parse(parts[1]);
        var enabled = int.Parse(parts[2]) == UnknownDrv;
        var flags = int.Parse(parts[3]);
        var info = parts[4];
        var disc = parts[5];
        var mount = parts.Length > 6 ? parts[6] : "";

        var driveVisible = (DriveVisible)visible;
        return new ParsedLine(MakeMkvOutputType.Drv, new DriveInfo(index, visible != 0, enabled, flags, info, disc, mount)
        {
            Attached = driveVisible != DriveVisible.NotAttached,
            Loaded = driveVisible is DriveVisible.Loaded or DriveVisible.Loading,
            IsOpen = driveVisible == DriveVisible.Open,
            MediaCd = flags == (int)DriveType.Cd,
            MediaDvd = flags == (int)DriveType.Dvd,
            MediaBd = flags is (int)DriveType.BdType1 or (int)DriveType.BdType2
        });
    }

    private static ParsedLine ParseCInfo(string content)
    {
        var parts = SplitCsv(content);
        return new ParsedLine(MakeMkvOutputType.CinFo,
            new CinFo(int.Parse(parts[0]), int.Parse(parts[1]), parts[2]));
    }

    private static ParsedLine ParseTInfo(string content)
    {
        var parts = SplitCsv(content);
        return new ParsedLine(MakeMkvOutputType.TInfo,
            new TInfo(int.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2]), parts[3]));
    }

    private static ParsedLine ParseSInfo(string content)
    {
        var parts = SplitCsv(content);
        return new ParsedLine(MakeMkvOutputType.SinFo,
            new SinFo(int.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2]), int.Parse(parts[3]), parts[4]));
    }

    private static ParsedLine ParsePrgV(string content)
    {
        var parts = content.Split(',');
        if (parts.Length >= 4)
        {
            // 4-field: current_title,total_titles,current_progress,total_progress
            var ct = int.TryParse(parts[0], out var v0) ? v0 : 0;
            var tt = int.TryParse(parts[1], out var v1) ? v1 : 0;
            var cp = int.TryParse(parts[2], out var v2) ? v2 : 0;
            var tp = int.TryParse(parts[3], out var v3) ? v3 : 0;
            return new ParsedLine(MakeMkvOutputType.PrgV, new PrgV(ct, tt, cp, tp));
        }
        else
        {
            // 3-field (MakeMKV v1.18+): ambiguous format — skip progress,
            // let the file-size monitor handle it reliably
            return new ParsedLine(MakeMkvOutputType.PrgV, new PrgV(0, 0, 0, 0));
        }
        return new ParsedLine(MakeMkvOutputType.PrgV, new PrgV(0, 0, 0, 0));
    }

    private static ParsedLine ParsePrgC(string content)
    {
        var parts = content.Split(',');
        var cp = parts.Length > 0 && int.TryParse(parts[0], out var v0) ? v0 : 0;
        var tp = parts.Length > 1 && int.TryParse(parts[1], out var v1) ? v1 : 0;
        return new ParsedLine(MakeMkvOutputType.PrgC, new PrgC(cp, tp));
    }

    private static ParsedLine ParsePrgT(string content)
    {
        // PRGT format: code,total,"text" — e.g. 5018,0,"Scanning CD-ROM devices"
        var parts = SplitCsv(content);
        if (parts.Length >= 2 && int.TryParse(parts[1], out var total2))
            return new ParsedLine(MakeMkvOutputType.PrgT, new PrgT(total2));

        // Fallback: try parsing as single int (legacy)
        if (int.TryParse(content, out var total1))
            return new ParsedLine(MakeMkvOutputType.PrgT, new PrgT(total1));

        return new ParsedLine(MakeMkvOutputType.PrgT, new PrgT(0));
    }

    private static string[] SplitCsv(string input)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < input.Length; i++)
        {
            var c = input[i];
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (c == ',' && !inQuotes)
            {
                result.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }
        result.Add(current.ToString());
        return [.. result];
    }

    private static int HmsToSeconds(string hms)
    {
        var parts = hms.Split(':');
        return int.Parse(parts[0]) * 3600 + int.Parse(parts[1]) * 60 + int.Parse(parts[2]);
    }
}

