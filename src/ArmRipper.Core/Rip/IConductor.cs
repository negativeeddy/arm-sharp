using ArmRipper.Core.Configuration;
using ArmRipper.Core.Models;

namespace ArmRipper.Core.Rip;

public interface IConductor
{
    Task<int> RunAsync(string devicePath, CancellationToken ct = default);

    /// <summary>
    /// Resumes a previously stopped/cancelled job from its last completed stage.
    /// Loads the existing job from the database and proceeds through the pipeline,
    /// skipping stages already marked in <see cref="Job.CompletedStages"/>.
    /// Does NOT create a new job — reuses the existing one.
    /// </summary>
    Task<int> RunResumeAsync(int jobId, CancellationToken ct = default);

    Task<int> RunForkedTranscodeAsync(int originalJobId, string rawFilePath, CancellationToken ct = default, DiscType? discType = null, VideoContentType? videoType = null, ArmSettings? effectiveSettings = null);

    /// <summary>
    /// Creates a new import job in the database — sets up the Job entity, config snapshot,
    /// and marks Setup/Identify/Rip stages complete. Does NOT run the transcode.
    /// Call <see cref="RunImportTranscodeForJobAsync"/> separately to actually transcode.
    /// </summary>
    Task<Job> CreateImportJobAsync(string rawFilePath, string title, string? year, VideoContentType? videoType, DiscType? discType, ArmSettings? effectiveSettings = null, CancellationToken ct = default);

    /// <summary>
    /// Runs transcode for an import job that has already been created in the DB
    /// (e.g. by <see cref="CreateImportJobAsync"/>). The job must have its config
    /// snapshot and stage markers set up already.
    /// </summary>
    Task<int> RunImportTranscodeForJobAsync(int jobId, CancellationToken ct = default);

    /// <summary>
    /// Creates a new standalone job from raw MKV files that were ripped on another machine,
    /// skipping the identify and rip stages — jumps straight to transcoding.
    /// The caller provides the movie metadata (title, year, video type) since there is no
    /// original job record to copy from.
    /// </summary>
    Task<int> RunImportTranscodeAsync(string rawFilePath, string title, string? year, VideoContentType? videoType, DiscType? discType, CancellationToken ct = default);
}
