using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using ArmMedia.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArmMedia.FileBotProvider;

/// <summary>
/// Invokes the FileBot CLI to identify TV episodes on a disc from raw media files.
/// Uses <c>filebot -rename --action test</c> in dry-run mode to get episode
/// mappings without modifying files on disk.
/// </summary>
public sealed partial class FileBotCliService
{
    private readonly FileBotCliRunner?        _runner;
    private readonly FileBotProviderOptions    _options;
    private readonly ILogger<FileBotCliService> _logger;

    /// <summary>
    /// Regex that matches a single filebot dry-run line:
    ///   [MOVE] /input/B1_t00.mkv → [/output/Series - S01E01 - Title.mkv]
    /// Group 1: source filename; Group 2: season (2-digit); Group 3: episodes; Group 4: title
    /// </summary>
    [GeneratedRegex(
        @"^\[MOVE\]\s+.*[/\\]([^/\\]+?\.\w+)\s*→\s*\[.*S(\d+)E(\d{2}(?:E\d{2})*)(?:\s*-\s*(.+?))?\]",
        RegexOptions.IgnoreCase)]
    private static partial Regex MovePattern();

    /// <summary>Initialises the service with an optional CLI runner, options, and logger.</summary>
    public FileBotCliService(
        FileBotCliRunner?                runner,
        ILogger<FileBotCliService>       logger,
        IOptions<FileBotProviderOptions> options)
    {
        _runner  = runner;
        _logger  = logger;
        _options = options.Value;
    }

    /// <summary>
    /// Tries to identify episodes by running FileBot against the media files on the disc.
    /// </summary>
    /// <param name="context">Disc context with track information.</param>
    /// <param name="rawFilePath">Path to the directory containing raw MakeMKV output files (.mkv).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>One <see cref="ProviderResult"/> per matched track, or empty if FileBot fails or is not installed.</returns>
    public async Task<ProviderResult[]> IdentifyAsync(
        DiscContext  context,
        string?      rawFilePath,
        CancellationToken ct = default)
    {
        if (_runner is null)
        {
            _logger.LogDebug("[FileBotCli] No CLI runner configured; skipping.");
            return [];
        }

        if (string.IsNullOrWhiteSpace(rawFilePath) || !Directory.Exists(rawFilePath))
        {
            _logger.LogDebug("[FileBotCli] Raw file path not available or does not exist; skipping.");
            return [];
        }

        // ── Step 1: Check if filebot is installed ─────────────────────────────
        FileBotCliOutput version;
        try
        {
            version = await _runner("-version", null, 10_000, ct);
        }
        catch
        {
            _logger.LogDebug("[FileBotCli] FileBot is not installed or not on PATH; skipping.");
            return [];
        }

        if (version.ExitCode != 0)
        {
            _logger.LogDebug("[FileBotCli] FileBot exit code {Code}; skipping.", version.ExitCode);
            return [];
        }

        _logger.LogInformation("[FileBotCli] FileBot detected: {Version}",
            version.StdOut?.Split('\n').FirstOrDefault()?.Trim() ?? "unknown");

        // ── Step 2: Try to match by series name + season ──────────────────────
        // Use TheTVDB as the database (best DVD order support).
        string db = _options.FileBotDb ?? "TheTVDB";
        string seriesFilter = !string.IsNullOrWhiteSpace(context.SeriesTitle)
            ? $" --q \"{EscapeArg(context.SeriesTitle)}\""
            : "";

        string format = $"{{s}} | S{{s00}}E{{e00}} | {{t}}";
        string args = $"-rename --db {db}{seriesFilter} --action test --format \"{format}\" \"{EscapeArg(rawFilePath)}\"";

        _logger.LogDebug("[FileBotCli] Running: filebot {Args}", args);

        FileBotCliOutput output;
        try
        {
            output = await _runner(args, rawFilePath, 120_000, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[FileBotCli] FileBot invocation failed.");
            return [];
        }

        if (output.ExitCode != 0)
        {
            _logger.LogDebug(
                "[FileBotCli] FileBot returned exit code {Code}. Stderr: {Stderr}",
                output.ExitCode,
                output.StdErr?.Split('\n').FirstOrDefault() ?? "(none)");
            return [];
        }

        if (string.IsNullOrWhiteSpace(output.StdOut))
        {
            _logger.LogDebug("[FileBotCli] FileBot produced no output.");
            return [];
        }

        // ── Step 3: Parse the dry-run output ───────────────────────────────────
        var results = ParseDryRun(output.StdOut, context, FileBotProvider.ProviderNameConst);

        if (results.Length == 0)
        {
            _logger.LogDebug("[FileBotCli] Could not parse any episode mappings from FileBot output.");
            return [];
        }

        _logger.LogInformation(
            "[FileBotCli] FileBot identified {Count} episode(s).", results.Length);
        return results;
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Parses the standard output of <c>filebot -rename --action test</c>
    /// into <see cref="ProviderResult"/> entries.
    /// </summary>
    internal static ProviderResult[] ParseDryRun(
        string       stdout,
        DiscContext  context,
        string       providerName)
    {
        var results = new List<ProviderResult>();
        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // Build a lookup from filename (without extension) to TrackContext for
        // matching FileBot output filenames back to physical tracks.
        var trackByFile = context.Tracks
            .Select(t => new
            {
                t.TrackIndex,
                FileName = Path.GetFileNameWithoutExtension(
                    t.RawProperties?.TryGetValue("FileName", out var f) == true
                        ? f
                        : $"track{t.TrackIndex:D2}"),
                FileNameUpper = (Path.GetFileNameWithoutExtension(
                    t.RawProperties?.TryGetValue("FileName", out var g) == true
                        ? g
                        : $"track{t.TrackIndex:D2}")).ToUpperInvariant()
            })
            .ToList();

        foreach (var line in lines)
        {
            var match = MovePattern().Match(line);
            if (!match.Success) continue;

            string srcFileName = match.Groups[1].Value.Trim();
            string seasonStr   = match.Groups[2].Value;
            string episodeStr  = match.Groups[3].Value;
            string title       = match.Groups[4].Value.Trim();

            if (!int.TryParse(seasonStr, out int season)) continue;

            // "01" or "01E02"
            var episodeNumbers = episodeStr.Split('E')
                .Select(s => int.TryParse(s, out var e) ? e : (int?)null)
                .Where(e => e.HasValue)
                .Select(e => e!.Value)
                .ToArray();

            if (episodeNumbers.Length == 0) continue;

            // Match back to a physical track by filename
            string srcBase = Path.GetFileNameWithoutExtension(srcFileName).ToUpperInvariant();
            var matched = trackByFile.FirstOrDefault(t => t.FileNameUpper == srcBase);

            results.Add(new ProviderResult
            {
                TrackIndex   = matched?.TrackIndex ?? -1,
                Season       = season,
                Episodes     = episodeNumbers,
                Title        = string.IsNullOrWhiteSpace(title) ? null : title,
                IsExtra      = season == 0,
                Confidence   = Confidence.High,
                ProviderName = providerName
            });
        }

        // Remove entries that couldn't be matched to a track
        return results
            .Where(r => r.TrackIndex >= 0)
            .OrderBy(r => r.TrackIndex)
            .ToArray();
    }

    /// <summary>Escapes a value for use in shell double-quoted arguments.</summary>
    private static string EscapeArg(string value)
    {
        return value.Replace("\"", "\\\"");
    }
}
