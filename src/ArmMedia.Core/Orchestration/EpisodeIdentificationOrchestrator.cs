using ArmMedia.Core.Abstractions;
using ArmMedia.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArmMedia.Core.Orchestration;

/// <summary>
/// Default implementation of <see cref="IEpisodeIdentificationOrchestrator"/>.
/// Runs the registered provider pipeline in configured order, merges results by
/// confidence, then applies multi-part and extras post-processing.
/// </summary>
public sealed class EpisodeIdentificationOrchestrator : IEpisodeIdentificationOrchestrator
{
    private readonly IReadOnlyList<IEpisodeIdentificationProvider> _providers;
    private readonly EpisodeIdentificationOptions                  _options;
    private readonly ILogger<EpisodeIdentificationOrchestrator>    _logger;

    /// <summary>Initialises the orchestrator with a DI-injected provider list.</summary>
    public EpisodeIdentificationOrchestrator(
        IEnumerable<IEpisodeIdentificationProvider>    providers,
        IOptions<EpisodeIdentificationOptions>          options,
        ILogger<EpisodeIdentificationOrchestrator>      logger)
    {
        _providers = providers
            .OrderBy(p => options.Value.ProviderOrder.IndexOf(p.ProviderName) is int i && i >= 0 ? i : int.MaxValue)
            .ToList();
        _options   = options.Value;
        _logger    = logger;
    }

    /// <inheritdoc/>
    public async Task<EpisodeMap> IdentifyAsync(DiscContext context, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting episode identification for disc '{DiscId}' ({Series} S{Season:D2})",
            context.DiscId, context.SeriesTitle, context.Season);

        // Track index → best result so far
        var bestResults = new Dictionary<int, ProviderResult>();

        foreach (var provider in _providers)
        {
            _logger.LogDebug("Calling provider '{Provider}'", provider.ProviderName);

            ProviderResult[] results;
            try
            {
                results = await provider.IdentifyAsync(context, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Provider '{Provider}' threw an exception; skipping.", provider.ProviderName);
                continue;
            }

            bool anyDefinitive = false;
            foreach (var result in results)
            {
                if (!bestResults.TryGetValue(result.TrackIndex, out var existing) ||
                    result.Confidence >= existing.Confidence)
                {
                    bestResults[result.TrackIndex] = result;
                    _logger.LogDebug("  Track {Track}: assigned {Provider} ({Confidence})",
                        result.TrackIndex, provider.ProviderName, result.Confidence);
                }

                if (result.Confidence == Confidence.Definitive)
                    anyDefinitive = true;
            }

            if (_options.ShortCircuitOnDefinitive && anyDefinitive)
            {
                _logger.LogInformation("Short-circuiting pipeline after definitive result from '{Provider}'.",
                    provider.ProviderName);
                break;
            }
        }

        // Fill unresolved tracks with positional fallback
        FillPositionalFallback(context, bestResults);

        // Post-processing
        var merged = ApplyMultiPartDetection(context, bestResults);
        ApplyExtrasDetection(context, merged);

        var mappedTracks = merged.Values
            .OrderBy(r => r.TrackIndex)
            .Select(r =>
            {
                var trackCtx = context.Tracks.FirstOrDefault(t => t.TrackIndex == r.TrackIndex);
                return new MappedTrack
                {
                    TrackIndex      = r.TrackIndex,
                    Season          = r.Season,
                    Episodes        = r.Episodes,
                    Title           = r.Title,
                    IsExtra         = r.IsExtra,
                    Duration        = trackCtx?.Duration,
                    SizeBytes       = trackCtx?.SizeBytes,
                    WinningProvider = r.ProviderName,
                    Confidence      = r.Confidence
                };
            })
            .ToList();

        _logger.LogInformation("Episode identification complete. {Count} tracks mapped.", mappedTracks.Count);

        return new EpisodeMap
        {
            SeriesTitle = context.SeriesTitle,
            Season      = context.Season,
            Tracks      = mappedTracks
        };
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static void FillPositionalFallback(DiscContext ctx, Dictionary<int, ProviderResult> results)
    {
        int episodeCounter = 1;
        foreach (var track in ctx.Tracks.OrderBy(t => t.TrackIndex))
        {
            if (!results.ContainsKey(track.TrackIndex))
            {
                results[track.TrackIndex] = new ProviderResult
                {
                    TrackIndex   = track.TrackIndex,
                    Season       = ctx.Season,
                    Episodes     = [episodeCounter],
                    Confidence   = Confidence.Low,
                    ProviderName = "PositionalFallback"
                };
            }
            episodeCounter++;
        }
    }

    private Dictionary<int, ProviderResult> ApplyMultiPartDetection(
        DiscContext ctx, Dictionary<int, ProviderResult> results)
    {
        // Clone so we can safely remove merged tracks
        var working = new Dictionary<int, ProviderResult>(results);
        var toRemove = new HashSet<int>();

        var ordered = working.Values.OrderBy(r => r.TrackIndex).ToList();

        for (int i = 0; i < ordered.Count - 1; i++)
        {
            var a = ordered[i];
            var b = ordered[i + 1];

            if (toRemove.Contains(a.TrackIndex) || toRemove.Contains(b.TrackIndex))
                continue;

            // Must be consecutive episodes, same season, confidence >= Medium
            if (a.Season != b.Season) continue;
            if (a.Confidence < Confidence.Medium || b.Confidence < Confidence.Medium) continue;
            if (b.Episodes[0] != a.Episodes[^1] + 1) continue;

            var aTrack = ctx.Tracks.FirstOrDefault(t => t.TrackIndex == a.TrackIndex);
            var bTrack = ctx.Tracks.FirstOrDefault(t => t.TrackIndex == b.TrackIndex);
            if (aTrack is null || bTrack is null) continue;

            // Duration delta check
            double durationDelta = Math.Abs((aTrack.Duration - bTrack.Duration).TotalSeconds);
            if (durationDelta > _options.MultiPartDurationToleranceSeconds) continue;

            _logger.LogDebug("Merging tracks {A} and {B} as multi-part episode [{Eps}]",
                a.TrackIndex, b.TrackIndex,
                string.Join(",", a.Episodes.Concat(b.Episodes)));

            // Merge B into A
            working[a.TrackIndex] = a with
            {
                Episodes = [..a.Episodes, ..b.Episodes],
                Title    = a.Title ?? b.Title
            };
            toRemove.Add(b.TrackIndex);
        }

        foreach (var idx in toRemove)
            working.Remove(idx);

        return working;
    }

    private void ApplyExtrasDetection(DiscContext ctx, Dictionary<int, ProviderResult> results)
    {
        foreach (var track in ctx.Tracks)
        {
            if (!results.TryGetValue(track.TrackIndex, out var result)) continue;
            if (result.IsExtra) continue; // already flagged by provider

            bool shortDuration = track.Duration.TotalSeconds < _options.ExtraMaxDurationSeconds;
            bool seasonZero    = result.Season == 0;

            if (shortDuration || seasonZero)
            {
                results[track.TrackIndex] = result with
                {
                    IsExtra = true,
                    Season  = 0
                };
                _logger.LogDebug("Track {Track} flagged as extra (short={Short}, s0={S0})",
                    track.TrackIndex, shortDuration, seasonZero);
            }
        }
    }
}
