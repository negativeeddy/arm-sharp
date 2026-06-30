using ArmMedia.Core.Abstractions;
using ArmMedia.Core.Models;
using ArmMedia.Linting;
using ArmMedia.Linting.Abstractions;
using ArmMedia.Naming;
using ArmMedia.Naming.Abstractions;
using Microsoft.Extensions.Logging;

namespace ArmMedia.ArmSharpExtensions;

/// <summary>
/// Extension methods that integrate the ArmMedia episode identification pipeline
/// into <c>ArmRipperService</c> from the ARM-Sharp host application.
/// Target framework: net10.0
/// </summary>
/// <remarks>
/// These extensions are designed to be called from within
/// <c>ArmRipperService.PrepareTranscodeInputPathAsync</c>.  The host project
/// references this assembly while the shared libraries remain on net9.0.
/// </remarks>
public static class ArmRipperServiceExtensions
{
    /// <summary>
    /// Resolves the output file path for a single disc track by running the full
    /// episode identification and naming pipeline.
    /// </summary>
    /// <param name="outputBasePath">
    /// The root output directory configured in ARM-Sharp (e.g., from
    /// <c>ArmRipperService.OutputBasePath</c>).
    /// </param>
    /// <param name="ctx">
    /// The disc context built from MakeMKV scan results and user configuration.
    /// <see cref="DiscContext.CurrentTrackIndex"/> must be set to the track being prepared.
    /// </param>
    /// <param name="orchestrator">Injected episode identification orchestrator.</param>
    /// <param name="renamer">Injected episode renamer.</param>
    /// <param name="namingOptions">Naming options (template, sanitisation, etc.).</param>
    /// <param name="lintingEngine">Optional linting engine; pass <c>null</c> to skip linting.</param>
    /// <param name="lintOptions">Linting options; ignored when <paramref name="lintingEngine"/> is <c>null</c>.</param>
    /// <param name="logger">Logger for diagnostic output.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The fully-qualified output file path (without extension).</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the current track cannot be found in the episode map, or when linting
    /// reports errors and <see cref="LintOptions.FailOnError"/> is <c>true</c>.
    /// </exception>
    public static async Task<string> PrepareTranscodeInputPathAsync(
        string                              outputBasePath,
        DiscContext                         ctx,
        IEpisodeIdentificationOrchestrator  orchestrator,
        IEpisodeRenamer                     renamer,
        NamingOptions                       namingOptions,
        ILintingEngine?                     lintingEngine,
        LintOptions?                        lintOptions,
        ILogger?                            logger,
        CancellationToken                   ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputBasePath);
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(orchestrator);
        ArgumentNullException.ThrowIfNull(renamer);
        ArgumentNullException.ThrowIfNull(namingOptions);

        if (ctx.CurrentTrackIndex is null)
            throw new ArgumentException($"{nameof(DiscContext)}.{nameof(DiscContext.CurrentTrackIndex)} must be set.", nameof(ctx));

        // ── Step 1: Run the identification pipeline ───────────────────────────
        logger?.LogInformation("[ArmSharp] Identifying episodes for disc '{DiscId}', track {Track}.",
            ctx.DiscId, ctx.CurrentTrackIndex);

        var episodeMap = await orchestrator.IdentifyAsync(ctx, ct);

        // ── Step 2: Optional linting ──────────────────────────────────────────
        if (lintingEngine is not null && lintOptions is not null)
        {
            var report = lintingEngine.Lint(episodeMap, lintOptions);
            if (report.HasErrors && lintOptions.FailOnError)
                throw new InvalidOperationException(
                    $"Episode map linting failed with {report.Issues.Count(i => i.Severity == Linting.Models.LintSeverity.Error)} error(s). " +
                    "Resolve lint issues or set Linting:FailOnError=false to continue.");

            if (report.HasWarnings && lintOptions.FailOnWarning)
                throw new InvalidOperationException(
                    $"Episode map linting failed with {report.Issues.Count(i => i.Severity == Linting.Models.LintSeverity.Warning)} warning(s). " +
                    "Resolve lint issues or set Linting:FailOnWarning=false to continue.");
        }

        // ── Step 3: Look up the current track ─────────────────────────────────
        var mappedTrack = episodeMap.Tracks
            .FirstOrDefault(t => t.TrackIndex == ctx.CurrentTrackIndex)
            ?? throw new InvalidOperationException(
                $"Track {ctx.CurrentTrackIndex} was not found in the episode map for disc '{ctx.DiscId}'.");

        // ── Step 4: Generate the output file name ─────────────────────────────
        namingOptions.SeriesTitle = string.IsNullOrWhiteSpace(namingOptions.SeriesTitle)
            ? episodeMap.SeriesTitle
            : namingOptions.SeriesTitle;

        string fileName  = renamer.Rename(mappedTrack, namingOptions);
        string fullPath  = Path.Combine(outputBasePath, fileName);

        logger?.LogInformation("[ArmSharp] Resolved output path: {Path}", fullPath);

        return fullPath;
    }
}
