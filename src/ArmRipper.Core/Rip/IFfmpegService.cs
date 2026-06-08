using ArmRipper.Core.Models;

namespace ArmRipper.Core.Rip;

public interface IFfmpegService
{
    Task TranscodeMkvAsync(Job job, string rawPath, string outputPath, IProgress<int>? progress = null, CancellationToken ct = default);
    Task TranscodeMainFeatureAsync(Job job, string rawPath, string outputPath, IProgress<int>? progress = null, CancellationToken ct = default);
    Task TranscodeAllAsync(Job job, string rawPath, string outputPath, IProgress<int>? progress = null, CancellationToken ct = default);
}
