using System.Text.Json;
using ArmRipper.Core.Infrastructure.Data;
using ArmRipper.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ArmRipper.Core.Rip;

/// <summary>
/// Caches TheDiscDb lookups in the local database so re-rips of the same disc
/// don't require a network call. Keyed by content hash.
/// </summary>
public sealed class DiscDbMappingService(
    ArmDbContext db,
    ILogger<DiscDbMappingService> logger) : IDiscDbMappingService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public async Task<DiscDbMediaResult?> GetCachedMappingAsync(string contentHash, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(contentHash))
            return null;

        try
        {
            var cached = await db.DiscDbMappings
                .AsNoTracking()
                .FirstOrDefaultAsync(m => m.ContentHash == contentHash, ct);

            if (cached is null)
                return null;

            var hashPreview = contentHash[..Math.Min(8, contentHash.Length)];
            logger.LogInformation(
                "DiscDb cache hit for hash {Hash}... (media: '{Title}' {Year})",
                hashPreview, cached.MediaTitle, cached.MediaYear);

            // Touch asynchronously — don't block the caller
            _ = TouchMappingAsync(contentHash, CancellationToken.None);

            return DeserializeMapping(cached);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "DiscDb cache lookup failed for hash {Hash}...",
                contentHash[..Math.Min(8, contentHash.Length)]);
            return null;
        }
    }

    public async Task SaveMappingAsync(string contentHash, DiscDbMediaResult result, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(contentHash) || result is null)
            return;

        try
        {
            // Avoid duplicates
            var existing = await db.DiscDbMappings
                .FirstOrDefaultAsync(m => m.ContentHash == contentHash, ct);

            if (existing is not null)
            {
                existing.LastUsedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                return;
            }

            var tracks = result.Releases?
                .SelectMany(r => r.Discs ?? [])
                .SelectMany(d => d.Titles ?? [])
                .Select(t => new
                {
                    t.Index,
                    t.Duration,
                    t.Size,
                    ItemTitle = t.Item?.Title,
                    Season = t.Item?.Season,
                    Episode = t.Item?.Episode,
                    ItemType = t.Item?.Type
                })
                .ToList();

            var mapping = new DiscDbMapping
            {
                ContentHash = contentHash,
                MediaSlug = result.Slug,
                MediaTitle = result.Title,
                MediaYear = result.Year,
                MediaType = result.Type,
                ImageUrl = result.ImageUrl,
                TrackMappingsJson = tracks.Count > 0 ? JsonSerializer.Serialize(tracks, JsonOptions) : null,
                LastUsedAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow
            };

            db.DiscDbMappings.Add(mapping);
            await db.SaveChangesAsync(ct);

            logger.LogInformation(
                "DiscDb cache saved for hash {Hash}... ('{Title}' {Year}) with {TrackCount} tracks",
                contentHash[..Math.Min(8, contentHash.Length)], result.Title, result.Year, tracks.Count);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "DiscDb cache save failed for hash {Hash}...",
                contentHash[..Math.Min(8, contentHash.Length)]);
        }
    }

    public async Task TouchMappingAsync(string contentHash, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(contentHash))
            return;

        try
        {
            await db.DiscDbMappings
                .Where(m => m.ContentHash == contentHash)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.LastUsedAt, DateTime.UtcNow), ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "DiscDb cache touch failed for hash {Hash}...",
                contentHash[..Math.Min(8, contentHash.Length)]);
        }
    }

    /// <summary>
    /// Deserializes a cached DiscDbMapping back into a DiscDbMediaResult.
    /// The TrackMappingsJson is deserialized into the releases/discs/titles hierarchy.
    /// </summary>
    private static DiscDbMediaResult? DeserializeMapping(DiscDbMapping cached)
    {
        var result = new DiscDbMediaResult
        {
            Id = 0, // Not stored in cache; TheDiscDb internal ID is ephemeral
            Title = cached.MediaTitle ?? "",
            Year = cached.MediaYear,
            Slug = cached.MediaSlug,
            ImageUrl = cached.ImageUrl,
            Type = cached.MediaType
        };

        if (string.IsNullOrWhiteSpace(cached.TrackMappingsJson))
            return result;

        try
        {
            var flatTracks = JsonSerializer.Deserialize<List<DiscDbFlatTrack>>(cached.TrackMappingsJson, JsonOptions);
            if (flatTracks is { Count: > 0 })
            {
                var titles = flatTracks.Select(t => new DiscDbTitle
                {
                    Index = t.TrackIndex,
                    Duration = t.DurationSeconds,
                    Size = t.FileSize,
                    Item = new DiscDbItem
                    {
                        Title = t.ItemTitle,
                        Season = t.Season,
                        Episode = t.Episode,
                        Type = t.ItemType
                    }
                }).ToList();

                var disc = new DiscDbDisc
                {
                    Index = 0,
                    Titles = titles
                };

                result.Releases =
                [
                    new DiscDbRelease
                    {
                        Discs = [disc]
                    }
                ];
            }
        }
        catch (JsonException)
        {
            // If deserialization fails, return the result without track data
        }

        return result;
    }
}
