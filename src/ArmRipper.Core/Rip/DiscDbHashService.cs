using System.Security.Cryptography;
using ArmRipper.Core.Models;
using Microsoft.Extensions.Logging;

namespace ArmRipper.Core.Rip;

/// <summary>
/// Computes TheDiscDb content hash from a mounted disc's filesystem.
/// The hash is an MD5 of each file's size (in bytes, little-endian) concatenated
/// in filename order. Only file sizes are used — no file content is read.
///
/// Reference: https://github.com/TheDiscDb/data/blob/main/tools/ImportBuddy/source/ImportBuddy/TheDiscDb.Core/DiscHash/HashingExtensions.cs
/// </summary>
[ArmMedia.Core.DiagnosticName(DiagnosticCategory)]
public sealed class DiscDbHashService(ILoggerFactory loggerFactory) : IDiscDbHashService
{
    private const string DiagnosticCategory = "DiscDbHashService";
    private readonly ILogger logger = loggerFactory.CreateLogger(DiagnosticCategory);
    public Task<string?> ComputeHashAsync(string mountPoint, DiscType discType, CancellationToken ct = default)
    {
        var (searchPath, pattern) = discType switch
        {
            DiscType.Dvd => (Path.Combine(mountPoint, "VIDEO_TS"), "*"),
            DiscType.Bluray => (Path.Combine(mountPoint, "BDMV", "STREAM"), "*.m2ts"),
            _ => (null, null)
        };

        if (searchPath is null || pattern is null)
        {
            logger.LogDebug("DiscDb hash: unsupported disc type {DiscType} at {MountPoint}", discType, mountPoint);
            return Task.FromResult<string?>(null);
        }

        if (!Directory.Exists(searchPath))
        {
            logger.LogWarning("DiscDb hash: search path does not exist: {SearchPath}", searchPath);
            return Task.FromResult<string?>(null);
        }

        try
        {
            var files = Directory.EnumerateFiles(searchPath, pattern)
                .Select(f => new FileInfo(f))
                .OrderBy(f => f.Name, StringComparer.Ordinal)
                .ToList();

            if (files.Count == 0)
            {
                logger.LogWarning("DiscDb hash: no files found in {SearchPath}", searchPath);
                return Task.FromResult<string?>(null);
            }

            using var hash = MD5.Create();
            foreach (var file in files)
            {
                var sizeBytes = BitConverter.GetBytes(file.Length);
                hash.TransformBlock(sizeBytes, 0, sizeBytes.Length, null, 0);
            }
            hash.TransformFinalBlock([], 0, 0);

            var hex = Convert.ToHexString(hash.Hash!);
            logger.LogInformation(
                "DiscDb hash: {Hash} from {FileCount} files in {SearchPath}",
                hex, files.Count, searchPath);
            return Task.FromResult<string?>(hex);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "DiscDb hash: failed to compute hash for {MountPoint}", mountPoint);
            return Task.FromResult<string?>(null);
        }
    }
}
