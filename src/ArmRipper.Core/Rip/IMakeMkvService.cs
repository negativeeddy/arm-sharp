using ArmRipper.Core.Models;

namespace ArmRipper.Core.Rip;

public interface IMakeMkvService
{
    Task EnsureKeyAsync(CancellationToken ct = default);
    IAsyncEnumerable<T> RunAsync<T>(string[] options, MakeMkvOutputType select, CancellationToken ct = default);
    /// <param name="infoMinLength">Optional override for the --minlength passed to makemkvcon info.
    /// When null (default), uses job.Config.MinLength or settings default. Pass 0 to get ALL tracks
    /// (used when DiscDb is enabled so short extras can be discovered and promoted).</param>
    Task<List<Track>> GetTrackInfoAsync(Job job, string baseName, int? infoMinLength = null, CancellationToken ct = default);
    Task<List<Track>> GetTrackInfoWithCacheAsync(Job job, string baseName, int? infoMinLength = null, CancellationToken ct = default);
    Task RipTrackAsync(Job job, string trackNumber, string outputPath, string mkvArgs, int minLength, IProgress<int>? progress = null, CancellationToken ct = default);
    Task RipAllTitlesAsync(Job job, string outputPath, string mkvArgs, int minLength, IProgress<int>? progress = null, CancellationToken ct = default);
}
