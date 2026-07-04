using ArmRipper.Core.Infrastructure.Data;
using ArmRipper.Core.Models;
using Microsoft.Extensions.Logging;

namespace ArmRipper.Core.Rip;

/// <summary>
/// Matches TheDiscDb title metadata to MakeMKV-identified tracks using
/// a combination of track index, duration, and file size matching.
/// </summary>
[ArmMedia.Core.DiagnosticName(DiagnosticCategory)]
public sealed class TrackMapperService(
    ArmDbContext db,
    ILoggerFactory loggerFactory) : ITrackMapperService
{
    private const string DiagnosticCategory = "TrackMapperService";
    private readonly ILogger logger = loggerFactory.CreateLogger(DiagnosticCategory);
    // Duration tolerance: ±5% or ±30s, whichever is larger
    private const double DurationTolerancePercent = 0.05;
    private const int DurationToleranceSeconds = 30;

    // Matching weights
    private const double IndexWeight = 0.60;
    private const double DurationWeight = 0.30;
    private const double SizeWeight = 0.10;

    public async Task<double> MapTracksAsync(Job job, DiscDbMediaResult? discDbResult, CancellationToken ct = default)
    {
        if (discDbResult?.Releases is null || discDbResult.Releases.Count == 0)
        {
            logger.LogInformation("TrackMapper: no DiscDb data to map for job {JobId}", job.Id);
            return 0.0;
        }

        // Flatten all titles from all releases/discs
        var dbTitles = discDbResult.Releases
            .SelectMany(r => r.Discs ?? [])
            .SelectMany(d => d.Titles ?? [])
            .ToList();

        if (dbTitles.Count == 0)
        {
            logger.LogInformation("TrackMapper: no DiscDb titles to map for job {JobId}", job.Id);
            return 0.0;
        }

        var tracks = job.Tracks.ToList();
        if (tracks.Count == 0)
        {
            logger.LogInformation("TrackMapper: no MakeMKV tracks to map for job {JobId}", job.Id);
            return 0.0;
        }

        logger.LogInformation(
            "TrackMapper: matching {TrackCount} MakeMKV tracks to {DbCount} DiscDb titles for job {JobId}",
            tracks.Count, dbTitles.Count, job.Id);

        var matchedCount = 0;
        var totalConfidence = 0.0;
        var unmatchedTracks = tracks.ToList(); // mutable pool — remove tracks as they're claimed

        foreach (var dbTitle in dbTitles)
        {
            var bestTrack = FindBestMatch(dbTitle, unmatchedTracks, out var bestConfidence);
            if (bestTrack is null || bestConfidence < 0.3)
                continue;

            bestTrack.EpisodeNumber = dbTitle.Item?.Episode;
            bestTrack.EpisodeTitle = dbTitle.Item?.Title;
            bestTrack.ContentType = dbTitle.Item?.Type ?? "unknown";
            bestTrack.TrackSeasonNumber = dbTitle.Item?.Season;
            bestTrack.DiscDbItemSlug = discDbResult.Slug;

            unmatchedTracks.Remove(bestTrack); // prevent this track from being claimed again
            matchedCount++;
            totalConfidence += bestConfidence;

            logger.LogDebug(
                "TrackMapper: track {TrackNum} '{FileName}' → '{Title}' S{Season}E{Episode} type={Type} (confidence {Conf:P1})",
                bestTrack.TrackNumber, bestTrack.FileName,
                bestTrack.EpisodeTitle, bestTrack.TrackSeasonNumber, bestTrack.EpisodeNumber,
                bestTrack.ContentType, bestConfidence);
        }

        await db.SaveChangesAsync(ct);

        var avgConfidence = matchedCount > 0 ? totalConfidence / matchedCount : 0.0;
        logger.LogInformation(
            "TrackMapper: matched {Matched}/{Total} tracks with avg confidence {Conf:P1} for job {JobId}",
            matchedCount, dbTitles.Count, avgConfidence, job.Id);

        return avgConfidence;
    }

    private static Track? FindBestMatch(DiscDbTitle dbTitle, List<Track> tracks, out double bestConfidence)
    {
        bestConfidence = 0.0;
        Track? bestTrack = null;

        foreach (var track in tracks)
        {
            var confidence = CalculateMatchConfidence(dbTitle, track);
            if (confidence > bestConfidence)
            {
                bestConfidence = confidence;
                bestTrack = track;
            }
        }

        return bestTrack;
    }

    private static double CalculateMatchConfidence(DiscDbTitle dbTitle, Track track)
    {
        // 1. Index match (strongest signal)
        var indexScore = 0.0;
        if (int.TryParse(track.TrackNumber, out var trackNum))
        {
            indexScore = trackNum == dbTitle.Index ? 1.0 : 0.0;
        }

        // 2. Duration match (medium signal)
        var durationScore = 0.0;
        if (dbTitle.Duration.HasValue && track.Length.HasValue && track.Length.Value > 0)
        {
            var dbSec = dbTitle.Duration.Value;
            var trackSec = track.Length.Value;
            var tolerance = Math.Max(DurationToleranceSeconds, dbSec * DurationTolerancePercent);
            var diff = Math.Abs(dbSec - trackSec);
            durationScore = diff <= tolerance
                ? 1.0 - (diff / (tolerance * 2))
                : 0.0;
        }

        // 3. File size match (weak signal, tiebreaker)
        var sizeScore = 0.0;
        if (dbTitle.Size.HasValue && track.FileSize.HasValue && track.FileSize.Value > 0)
        {
            var sizeDiff = Math.Abs((double)(dbTitle.Size.Value - track.FileSize.Value));
            var maxSize = Math.Max(dbTitle.Size.Value, track.FileSize.Value);
            sizeScore = maxSize > 0
                ? Math.Max(0.0, 1.0 - (sizeDiff / maxSize) * 5)
                : 0.0;
        }

        return indexScore * IndexWeight
             + durationScore * DurationWeight
             + sizeScore * SizeWeight;
    }
}
