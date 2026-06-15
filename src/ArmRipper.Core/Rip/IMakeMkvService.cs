using System.Runtime.CompilerServices;
using ArmRipper.Core.Models;

namespace ArmRipper.Core.Rip;

public interface IMakeMkvService
{
    Task EnsureKeyAsync(CancellationToken ct = default);
    IAsyncEnumerable<T> RunAsync<T>(string[] options, MakeMkvOutputType select, [EnumeratorCancellation] CancellationToken ct = default);
    Task<List<Track>> GetTrackInfoAsync(Job job, string baseName, CancellationToken ct = default);
    Task<List<Track>> GetTrackInfoWithCacheAsync(Job job, string baseName, CancellationToken ct = default);
    Task RipTrackAsync(Job job, string trackNumber, string outputPath, string mkvArgs, int minLength, IProgress<int>? progress = null, CancellationToken ct = default);
    Task RipAllTitlesAsync(Job job, string outputPath, string mkvArgs, int minLength, IProgress<int>? progress = null, CancellationToken ct = default);
}
