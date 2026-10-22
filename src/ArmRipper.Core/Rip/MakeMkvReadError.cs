namespace ArmRipper.Core.Rip;

/// <summary>
/// Captured details from a MakeMKV MSG:2003 read-error line. Used to
/// distinguish recoverable errors (CRC/ECC that were worked around) from
/// unrecoverable medium errors that indicate physical disc damage.
/// </summary>
public sealed record MakeMkvReadErrorDetail(string RawMessage, string? File, string? Offset, string? Sense);

public static class MakeMkvReadErrorClassifier
{
    /// <summary>
    /// Returns true when the error indicates unrecoverable medium damage rather
    /// than a recoverable/read-around condition.
    /// </summary>
    public static bool IsLikelyDiscDamage(string? message, string? sense)
    {
        var combined = string.Join(" ", message ?? string.Empty, sense ?? string.Empty).ToUpperInvariant();

        if (combined.Contains("MEDIUM ERROR:1090") ||
            combined.Contains("UNRECOVERED READ ERROR") ||
            combined.Contains("POSIX ERROR - INPUT/OUTPUT ERROR") ||
            combined.Contains("INPUT/OUTPUT ERROR"))
            return true;

        // CRC OR ECC ERROR alone is often recoverable/worked-around by MakeMKV.
        // A plain "MEDIUM ERROR" with no explicit CRC/ECC text and no sense
        // detail is suspicious, but prefer explicit signals above.
        if (combined.Contains("MEDIUM ERROR") && !combined.Contains("CRC") && !combined.Contains("ECC"))
            return true;

        return false;
    }

    /// <summary>
    /// Extracts best-effort details from a MSG:2003 line so they can be reported
    /// to the user without dumping raw makeMkvcon output.
    /// </summary>
    public static MakeMkvReadErrorDetail Extract(MakeMkvMessage msg)
    {
        var raw = msg.Message ?? string.Empty;
        var file = msg.Params.Length > 1 ? msg.Params[1] : null;
        var offset = msg.Params.Length > 2 ? msg.Params[2] : null;

        // The sense string is often embedded in params[0] (e.g. "Scsi error -
        // MEDIUM ERROR:1090" or "Posix error - Input/output error").
        var sense = msg.Params.Length > 0 ? msg.Params[0] : null;

        // Also try to pull a path like /BDMV/STREAM/XXX.m2ts from the message text.
        if (string.IsNullOrWhiteSpace(file))
        {
            var match = System.Text.RegularExpressions.Regex.Match(
                raw, "'([^']+\\.(m2ts|mts|vob|mkv|iso))'", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success)
                file = match.Groups[1].Value;
        }

        if (string.IsNullOrWhiteSpace(offset))
        {
            var match = System.Text.RegularExpressions.Regex.Match(raw, "at offset ['\"]?(\\d+)['\"]?", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success)
                offset = match.Groups[1].Value;
        }

        return new MakeMkvReadErrorDetail(raw, file, offset, sense);
    }
}
