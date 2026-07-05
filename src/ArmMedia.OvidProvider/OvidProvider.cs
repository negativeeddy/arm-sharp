using ArmMedia.Core.Abstractions;
using ArmMedia.Core.Models;
using ArmMedia.OvidProvider.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArmMedia.OvidProvider;

/// <summary>
/// An <see cref="IEpisodeIdentificationProvider"/> that looks up disc metadata
/// via the OVID REST API.
///
/// When a matching OVID disc record is found, returns results with
/// <see cref="Confidence.Definitive"/> — the OVID fingerprint is a structural
/// hash of the disc itself, so a match is an exact disc identification.
///
/// This provider is designed to be registered after the DiscDb provider and
/// before runtime-based providers (DvdCompare, Tmdb, etc.) in the pipeline.
/// </summary>
public sealed class OvidProvider : IEpisodeIdentificationProvider
{
    private readonly OvidApiClient _apiClient;
    private readonly OvidProviderOptions _options;
    private readonly ILogger<OvidProvider> _logger;

    /// <summary>Initialises the provider with an OVID API client.</summary>
    public OvidProvider(
        OvidApiClient apiClient,
        IOptions<OvidProviderOptions> options,
        ILogger<OvidProvider> logger)
    {
        _apiClient = apiClient;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc/>
    public string ProviderName => "Ovid";

    /// <inheritdoc/>
    public async Task<ProviderResult[]> IdentifyAsync(
        DiscContext context,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            _logger.LogDebug("[OvidProvider] Disabled via configuration.");
            return [];
        }

        // Need an OVID fingerprint to look up
        var fingerprint = context.OvidFingerprint;
        if (string.IsNullOrWhiteSpace(fingerprint))
        {
            _logger.LogDebug(
                "[OvidProvider] No OVID fingerprint available in DiscContext for disc '{DiscId}'.",
                context.DiscId);
            return [];
        }

        // Query the OVID API
        var record = await _apiClient.LookupByFingerprintAsync(fingerprint, cancellationToken);

        if (record is null)
        {
            _logger.LogDebug(
                "[OvidProvider] No OVID record found for fingerprint '{Fingerprint}'.",
                fingerprint);
            return [];
        }

        _logger.LogInformation(
            "[OvidProvider] Found OVID record '{Title}' ({Year}) for fingerprint '{Fingerprint}'.",
            record.Release?.Title, record.Release?.Year, fingerprint);

        // Map OVID titles to provider results
        var results = MapResults(record, context);
        return results;
    }

    private ProviderResult[] MapResults(
        OvidDiscLookupResponse record,
        DiscContext context)
    {
        if (record.Titles.Count == 0)
        {
            _logger.LogDebug(
                "[OvidProvider] OVID record '{Fingerprint}' has no title entries.",
                record.Fingerprint);
            return [];
        }

        var results = new List<ProviderResult>(record.Titles.Count);

        foreach (var title in record.Titles)
        {
            // Match track index by position or duration
            var trackIndex = title.TitleIndex;
            if (trackIndex < 0 || trackIndex >= context.Tracks.Count)
            {
                // If the index is out of range, try matching by duration
                var matchedByDuration = FindTrackByDuration(context, title.DurationSecs);
                if (matchedByDuration.HasValue)
                    trackIndex = matchedByDuration.Value;
                else
                    continue; // Can't map this title
            }

            bool isExtra = title.TitleType is "extra" or "trailer" or "deleted_scene" or "commentary"
                or "menu" or "preview" or "promo";

            // If OVID says it's the main feature and it's not marked as an extra type,
            // treat it as a regular episode
            bool isMainFeature = title.IsMainFeature && !isExtra;

            results.Add(new ProviderResult
            {
                TrackIndex = trackIndex,
                Season = isExtra || !isMainFeature ? 0 : context.Season,
                Episodes = isMainFeature ? [TrackIndexToEpisode(trackIndex, context)] : [],
                Title = title.DisplayName ?? title.TitleType,
                IsExtra = isExtra || !isMainFeature,
                Confidence = Confidence.Definitive,
                ProviderName = ProviderName,
            });
        }

        _logger.LogInformation(
            "[OvidProvider] Mapped {Count} track(s) from OVID record '{Fingerprint}'.",
            results.Count, record.Fingerprint);

        return results.ToArray();
    }

    /// <summary>
    /// Find a track in the context by matching its duration (within tolerance).
    /// </summary>
    private static int? FindTrackByDuration(DiscContext context, int? durationSecs)
    {
        if (durationSecs is null or <= 0)
            return null;

        // Try exact match first, then within tolerance
        const int toleranceSeconds = 5;
        for (int i = 0; i < context.Tracks.Count; i++)
        {
            var trackDuration = (int)context.Tracks[i].Duration.TotalSeconds;
            if (Math.Abs(trackDuration - durationSecs.Value) <= toleranceSeconds)
                return i;
        }

        return null;
    }

    /// <summary>
    /// Convert a physical track index to an episode number, using the
    /// <see cref="DiscContext.StartingEpisodeNumber"/> if available.
    /// </summary>
    private static int TrackIndexToEpisode(int trackIndex, DiscContext context)
    {
        if (context.StartingEpisodeNumber.HasValue)
            return context.StartingEpisodeNumber.Value + trackIndex;

        // Default: 1-based episode numbering matching physical order
        return trackIndex + 1;
    }
}
