using ArmRipper.Core.Infrastructure;
using ArmRipper.Core.Models;

namespace ArmRipper.Core.Rip;

public interface IHandBrakeService
{
    Task<CliResult> TranscodeMkvAsync(Job job, string rawPath, string outputPath, CancellationToken ct = default);
    Task<CliResult> TranscodeMainFeatureAsync(Job job, string rawPath, string outputPath, CancellationToken ct = default);
    Task<CliResult> TranscodeAllAsync(Job job, string rawPath, string outputPath, CancellationToken ct = default);
}
