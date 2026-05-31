using ArmRipper.Core.Models;

namespace ArmRipper.Core.Rip;

public interface IFfmpegService
{
    Task TranscodeMkvAsync(Job job, string rawPath, string outputPath, CancellationToken ct = default);
    Task TranscodeMainFeatureAsync(Job job, string rawPath, string outputPath, CancellationToken ct = default);
    Task TranscodeAllAsync(Job job, string rawPath, string outputPath, CancellationToken ct = default);
}
