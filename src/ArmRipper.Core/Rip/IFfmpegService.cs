using ArmRipper.Core.Infrastructure;
using ArmRipper.Core.Models;

namespace ArmRipper.Core.Rip;

public interface IFfmpegService
{
    Task<string> GetVersionAsync(CancellationToken ct = default);
    Task<CliResult> TranscodeMkvAsync(Job job, string rawPath, string outputPath, IProgress<int>? progress = null, CancellationToken ct = default);
    Task<CliResult> TranscodeMainFeatureAsync(Job job, string rawPath, string outputPath, IProgress<int>? progress = null, CancellationToken ct = default);
    Task<CliResult> TranscodeAllAsync(Job job, string rawPath, string outputPath, IProgress<int>? progress = null, CancellationToken ct = default);
    /// <summary>
    /// Probes a media file with ffprobe and returns its duration in seconds,
    /// or null when the file is missing, ffprobe fails, or the duration cannot be parsed.
    /// </summary>
    Task<double?> ProbeDurationAsync(string filePath, CancellationToken ct = default);
}
