using ArmMedia.Core.Abstractions;
using ArmMedia.Core.Models;
using Microsoft.Extensions.Logging;

namespace ArmMedia.DiscDbProvider;

/// <summary>
/// An <see cref="IEpisodeIdentificationProvider"/> that looks up disc metadata
/// via a configured <see cref="IDiscDbLookupService"/>.
/// When a matching DiscDb record is found, returns results with
/// <see cref="Confidence.Definitive"/>, causing the orchestrator to short-circuit.
/// </summary>
public sealed class DiscDbProvider : IEpisodeIdentificationProvider
{
    private readonly IDiscDbLookupService _discDbLookup;
    private readonly ILogger<DiscDbProvider> _logger;

    /// <summary>Initialises the provider with a DiscDb lookup service.</summary>
    public DiscDbProvider(
        IDiscDbLookupService discDbLookup,
        ILogger<DiscDbProvider> logger)
    {
        _discDbLookup = discDbLookup;
        _logger = logger;
    }

    /// <inheritdoc/>
    public string ProviderName => "DiscDb";

    /// <inheritdoc/>
    public async Task<ProviderResult[]> IdentifyAsync(
        DiscContext context,
        CancellationToken cancellationToken = default)
    {
        // Look up the disc by its content hash (DiscId).
        var record = await _discDbLookup.LookupDiscAsync(
            context.DiscId, cancellationToken);

        if (record is null)
        {
            _logger.LogDebug(
                "[DiscDbProvider] No DiscDb record found for hash '{DiscId}'.",
                context.DiscId);
            return [];
        }

        _logger.LogInformation(
            "[DiscDbProvider] Found DiscDb record '{Title}' ({Year}) for hash '{DiscId}'.",
            record.Title, record.Year, context.DiscId);

        // Map the slim lookup DTOs directly to provider results.
        var results = record.Tracks.Select(track =>
        {
            int season = track.Season ?? context.Season;
            int episode = track.Episode ?? 0;
            bool isExtra = track.ContentType is "extra" or "trailer" or "deleted_scene" or "commentary";

            return new ProviderResult
            {
                TrackIndex   = track.TrackIndex,
                Season       = isExtra ? 0 : season,
                Episodes     = [episode],
                Title        = track.Title,
                IsExtra      = isExtra,
                Confidence   = Confidence.Definitive,
                ProviderName = ProviderName
            };
        }).ToArray();

        _logger.LogInformation(
            "[DiscDbProvider] Mapped {Count} track(s) from DiscDb record.",
            results.Length);

        return results;
    }
}
