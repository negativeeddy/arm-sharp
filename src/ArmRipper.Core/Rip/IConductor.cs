namespace ArmRipper.Core.Rip;

public interface IConductor
{
    Task<int> RunAsync(string devicePath, CancellationToken ct = default);
    Task<int> RunForkedTranscodeAsync(int originalJobId, string rawFilePath, CancellationToken ct = default);

    /// <summary>
    /// Creates a new standalone job from raw MKV files that were ripped on another machine,
    /// skipping the identify and rip stages — jumps straight to transcoding.
    /// The caller provides the movie metadata (title, year, video type) since there is no
    /// original job record to copy from.
    /// </summary>
    /// <param name="rawFilePath">Path to the raw .mkv file (or parent directory) to transcode.</param>
    /// <param name="title">Movie/show title from user search selection.</param>
    /// <param name="year">Release year (optional).</param>
    /// <param name="videoType">"movie", "series", or "tv" (optional, defaults to "movie").</param>
    Task<int> RunImportTranscodeAsync(string rawFilePath, string title, string? year, string? videoType, CancellationToken ct = default);
}
