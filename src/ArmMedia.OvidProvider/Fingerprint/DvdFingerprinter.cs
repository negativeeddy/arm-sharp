using System.Security.Cryptography;
using System.Text;

namespace ArmMedia.OvidProvider.Fingerprint;

/// <summary>
/// Implements the OVID-DVD-1 fingerprint algorithm.
///
/// Builds a deterministic canonical string from parsed IFO structures,
/// then computes SHA-256 → first 40 hex chars → "dvd1-" prefix.
///
/// Specification: OVID-DVD-1 (§2.1)
///   Canonical string format (pipe-delimited, no spaces):
///     OVID-DVD-1|{VTS_count}|{title_count}|{vts1_block}|{vts2_block}|...
///
///   Each VTS block:
///     {pgc_count}:{pgc1_entry},{pgc2_entry},...
///
///   Each PGC entry:
///     {dur}:{chaps}:{audio}:{subs}
///
/// Where audio and subs are comma-joined language codes from VTS-level streams.
/// </summary>
public static class DvdFingerprinter
{
    /// <summary>
    /// Build the OVID-DVD-1 canonical string from parsed VMG and VTS data.
    /// </summary>
    public static string BuildCanonicalString(VmgInfo vmg, IReadOnlyList<VtsInfo> vtsList)
    {
        var parts = new List<string>
        {
            "OVID-DVD-1",
            vmg.VtsCount.ToString(),
            vmg.TitleCount.ToString(),
        };

        foreach (var vts in vtsList)
        {
            var audioStr = string.Join(",", vts.AudioStreams.Select(s => s.Language));
            var subsStr = string.Join(",", vts.SubtitleStreams.Select(s => s.Language));

            var pgcEntries = new List<string>();
            foreach (var pgc in vts.PgcList)
            {
                pgcEntries.Add($"{pgc.DurationSeconds}:{pgc.ChapterCount}:{audioStr}:{subsStr}");
            }

            var pgcCount = vts.PgcList.Count;

            if (pgcEntries.Count > 0)
            {
                parts.Add($"{pgcCount}:{string.Join(",", pgcEntries)}");
            }
            else
            {
                // VTS with no PGCs — just the count
                parts.Add(pgcCount.ToString());
            }
        }

        return string.Join("|", parts);
    }

    /// <summary>
    /// Compute the SHA-256 fingerprint from a canonical string.
    /// Returns "dvd1-{first 40 hex characters}".
    /// </summary>
    public static string ComputeFingerprint(string canonicalString)
    {
        var bytes = Encoding.UTF8.GetBytes(canonicalString);
        var hash = SHA256.HashData(bytes);
        var hex = Convert.ToHexString(hash).ToLowerInvariant();
        return $"dvd1-{hex[..40]}";
    }

    /// <summary>
    /// Compute the full OVID-DVD-1 fingerprint from parsed disc data.
    /// </summary>
    public static string Fingerprint(VmgInfo vmg, IReadOnlyList<VtsInfo> vtsList)
    {
        var canonical = BuildCanonicalString(vmg, vtsList);
        return ComputeFingerprint(canonical);
    }
}
