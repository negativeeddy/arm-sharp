using Microsoft.Extensions.Logging;

namespace ArmMedia.OvidProvider.Fingerprint;

/// <summary>
/// Disc format types supported for OVID fingerprinting.
/// </summary>
public enum OvidDiscFormat
{
    /// <summary>DVD-Video disc (VIDEO_TS / IFO-based).</summary>
    Dvd,

    /// <summary>Blu-ray disc (BDMV / MPLS-based) — not yet implemented.</summary>
    Bluray,
}

/// <summary>
/// Result of computing an OVID fingerprint for a disc.
/// </summary>
public sealed record OvidFingerprintResult
{
    /// <summary>The primary OVID fingerprint (e.g. "dvd1-a3f92c1b...").</summary>
    public required string Fingerprint { get; init; }

    /// <summary>The canonical string used to generate the fingerprint.</summary>
    public required string CanonicalString { get; init; }

    /// <summary>Number of VTS sets found on the disc.</summary>
    public required int VtsCount { get; init; }

    /// <summary>Number of titles found on the disc.</summary>
    public required int TitleCount { get; init; }

    /// <summary>Whether the fingerprint was successfully computed.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsSuccess => Fingerprint != Unknown;

    /// <summary>Sentinel value returned when fingerprinting fails.</summary>
    public const string Unknown = "unknown";
}

/// <summary>
/// High-level service for computing OVID fingerprints from a mounted disc's filesystem.
/// Handles both DVD (IFO-based) and Blu-ray (BDMV/MPLS-based) discs.
/// </summary>
public sealed class OvidDisc
{
    private readonly ILogger<OvidDisc>? _logger;

    /// <summary>Initialises the OVID fingerprint service.</summary>
    /// <param name="logger">Optional logger instance.</param>
    public OvidDisc(ILogger<OvidDisc>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Compute an OVID fingerprint from a mounted disc.
    /// </summary>
    /// <param name="mountPoint">Path to the mounted disc (e.g., "/mnt/dev/sr0").</param>
    /// <param name="format">The disc format (Dvd or Bluray).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An <see cref="OvidFingerprintResult"/> with the computed fingerprint, or
    /// <see cref="OvidFingerprintResult.Unknown"/> if fingerprinting failed.</returns>
    public async Task<OvidFingerprintResult> ComputeAsync(
        string mountPoint,
        OvidDiscFormat format,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (format == OvidDiscFormat.Dvd)
                return await ComputeDvdAsync(mountPoint, cancellationToken);

            _logger?.LogWarning(
                "OVID fingerprinting not yet supported for disc format {Format}", format);
            return Failed();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to compute OVID fingerprint at {MountPoint}", mountPoint);
            return Failed();
        }
    }

    private async Task<OvidFingerprintResult> ComputeDvdAsync(
        string mountPoint,
        CancellationToken cancellationToken)
    {
        var videoTs = FindVideoTs(mountPoint);
        if (videoTs is null)
        {
            _logger?.LogWarning("OVID: No VIDEO_TS directory found at {MountPoint}", mountPoint);
            return Failed();
        }

        // Read VIDEO_TS.IFO
        var vmgPath = Path.Combine(videoTs, "VIDEO_TS.IFO");
        if (!File.Exists(vmgPath))
        {
            _logger?.LogWarning("OVID: VIDEO_TS.IFO not found in {VideoTs}", videoTs);
            return Failed();
        }

        var vmgData = await File.ReadAllBytesAsync(vmgPath, cancellationToken);
        var vmg = IfoParser.ParseVmg(vmgData);

        if (vmg.VtsCount == 0)
        {
            _logger?.LogWarning("OVID: VMG reports 0 VTS at {VideoTs}", videoTs);
            return Failed();
        }

        // Parse each VTS in order
        var vtsList = new List<VtsInfo>(vmg.VtsCount);
        for (int i = 1; i <= vmg.VtsCount; i++)
        {
            var vtsName = $"VTS_{i:D2}_0.IFO";
            var vtsPath = Path.Combine(videoTs, vtsName);
            if (!File.Exists(vtsPath))
            {
                _logger?.LogWarning("OVID: {VtsName} not found (expected {Count} VTS sets)", vtsName, vmg.VtsCount);
                // Try to continue with partial data
                vtsList.Add(new VtsInfo());
                continue;
            }

            var vtsData = await File.ReadAllBytesAsync(vtsPath, cancellationToken);
            var vts = IfoParser.ParseVts(vtsData);
            vtsList.Add(vts);
        }

        var canonical = DvdFingerprinter.BuildCanonicalString(vmg, vtsList);
        var fingerprint = DvdFingerprinter.ComputeFingerprint(canonical);

        _logger?.LogInformation(
            "OVID fingerprint computed: {Fingerprint} ({VtsCount} VTS, {TitleCount} titles) from {VideoTs}",
            fingerprint, vmg.VtsCount, vmg.TitleCount, videoTs);

        return new OvidFingerprintResult
        {
            Fingerprint = fingerprint,
            CanonicalString = canonical,
            VtsCount = vmg.VtsCount,
            TitleCount = vmg.TitleCount,
        };
    }

    /// <summary>
    /// Locate the VIDEO_TS directory from a mount point.
    /// </summary>
    private static string? FindVideoTs(string mountPoint)
    {
        // Direct VIDEO_TS subdirectory
        var direct = Path.Combine(mountPoint, "VIDEO_TS");
        if (Directory.Exists(direct))
            return direct;

        // Case-insensitive fallback
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(mountPoint))
            {
                var name = Path.GetFileName(dir);
                if (string.Equals(name, "VIDEO_TS", StringComparison.OrdinalIgnoreCase))
                    return dir;
            }
        }
        catch
        {
            // Ignore enumeration errors
        }

        return null;
    }

    private static OvidFingerprintResult Failed()
        => new()
        {
            Fingerprint = OvidFingerprintResult.Unknown,
            CanonicalString = "",
            VtsCount = 0,
            TitleCount = 0,
        };
}
