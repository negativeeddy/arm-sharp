using ArmMedia.Core.Models;
using ArmMedia.Naming.Abstractions;
using System.Text;
using System.Text.RegularExpressions;

namespace ArmMedia.Naming;

/// <summary>
/// Default implementation of <see cref="IEpisodeRenamer"/> that processes
/// simple token-substitution templates defined in <see cref="NamingOptions"/>.
/// </summary>
public sealed partial class DefaultEpisodeRenamer : IEpisodeRenamer
{
    // Characters that are invalid in a single file or folder name component.
    // '/' and '\\' (path separators) are NOT included because templates like
    // Plex intentionally produce relative paths with directory separators.
    // However, token values (series title, episode title) are sanitized
    // separately to prevent injected path separators.
    private static readonly char[] InvalidFileNameChars =
        Path.GetInvalidFileNameChars()
            .Concat(":<>\"|?*")
            .Where(c => c != '/' && c != '\\')
            .Distinct()
            .ToArray();

    // Characters that are invalid even within a path component — includes
    // path separators to prevent injection through substituted tokens.
    private static readonly char[] InvalidTokenChars =
        InvalidFileNameChars.Concat(['/', '\\']).Distinct().ToArray();

    /// <inheritdoc/>
    public string Rename(MappedTrack track, NamingOptions options)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(options);

        string template = track.IsExtra
            ? options.ExtraTemplate
            : track.IsMultiPart
                ? options.MultiPartTemplate
                : options.Template;

        string series = string.IsNullOrWhiteSpace(options.SeriesTitle)
            ? "Unknown Series"
            : options.SeriesTitle;

        string episodesToken = track.IsMultiPart
            ? string.Concat(track.Episodes.Select(e => $"{options.MultiPartSep}{e:D2}"))
            : $"{track.Episodes[0]:D2}";

        // Sanitize substituted token values when enabled, so injected path
        // separators are removed while template path separators are preserved.
        string sanitizedSeries = options.SanitizeFileName ? SanitizeToken(series)       : series;
        string sanitizedTitle  = options.SanitizeFileName ? SanitizeToken(track.Title ?? "Unknown") : (track.Title ?? "Unknown");

        var result = template
            .Replace("{Series}",    sanitizedSeries,                                   StringComparison.OrdinalIgnoreCase)
            .Replace("{Season:D2}", track.Season.ToString("D2"),                       StringComparison.OrdinalIgnoreCase)
            .Replace("{Season}",    track.Season.ToString(),                           StringComparison.OrdinalIgnoreCase)
            .Replace("{Episode:D2}", track.Episodes[0].ToString("D2"),                StringComparison.OrdinalIgnoreCase)
            .Replace("{Episode}",   track.Episodes[0].ToString(),                      StringComparison.OrdinalIgnoreCase)
            .Replace("{Episodes}",  episodesToken,                                     StringComparison.OrdinalIgnoreCase)
            .Replace("{Title}",     sanitizedTitle,                                    StringComparison.OrdinalIgnoreCase);

        // Final pass: only strip truly path-invalid chars (not '/' or '\\')
        return options.SanitizeFileName ? SanitizePath(result) : result;
    }

    /// <summary>
    /// Sanitizes a token value so it doesn't introduce path separators or
    /// other invalid characters when substituted into the template.
    /// </summary>
    private static string SanitizeToken(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (char c in value)
            sb.Append(Array.IndexOf(InvalidTokenChars, c) >= 0 ? '_' : c);
        return sb.ToString().Trim(' ', '.');
    }

    /// <summary>
    /// Final sanitization pass that only strips characters invalid within
    /// a file system path (preserves '/' and '\\' as path separators).
    /// </summary>
    private static string SanitizePath(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
            sb.Append(Array.IndexOf(InvalidFileNameChars, c) >= 0 ? '_' : c);
        return sb.ToString().Trim(' ', '.');
    }
}
