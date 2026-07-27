# Recommendations for ARM Sharp

**Based on:** MKV Auto Release feature comparison  
**Date:** 2026-07-26  
**Status:** Draft for review

---

## Executive Summary

ARM Sharp has a solid architectural foundation with its provider pipeline, multi-source metadata, and .NET ecosystem. However, it significantly lags behind MKV Auto in TV series workflow, collection management, and user experience. This document outlines actionable recommendations to close those gaps while maintaining ARM Sharp's architectural strengths.

---

## Priority 1: TMDB Season/Episode Catalog Integration

**Impact:** Critical — Enables TV series workflow  
**Effort:** Medium (1-2 weeks)  
**Risk:** Low

### Problem
ARM Sharp currently relies entirely on DiscDb for episode metadata. When DiscDb misses, users must manually enter every season/episode number. MKV Auto solves this with TMDB season/episode catalogs.

### Solution
Extend `TmdbService` and create a `TmdbEpisodeProvider`:

```csharp
// New methods for TmdbService
public async Task<TmdbTvDetails> GetTvDetailsAsync(int tmdbId, CancellationToken ct = default)
{
    // GET /tv/{id} → number_of_seasons, name, first_air_date
}

public async Task<IReadOnlyList<TmdbEpisode>> GetTvSeasonEpisodesAsync(
    int tmdbId, int seasonNumber, CancellationToken ct = default)
{
    // GET /tv/{id}/season/{n} → episodes[]
    // Each episode: season_number, episode_number, name, overview, air_date, runtime
}
```

### Implementation Steps
1. Add `TmdbTvDetails` and `TmdbEpisode` models to `ArmMedia.TmdbProvider`
2. Implement `GetTvDetailsAsync` and `GetTvSeasonEpisodesAsync` in `TmdbService`
3. Create `TmdbEpisodeProvider : IEpisodeIdentificationProvider` in `ArmMedia.TmdbProvider`
4. Register in DI and add to `EpisodeIdentificationOrchestrator` pipeline
5. Add season/episode selection UI component

### Integration with Existing Pipeline
```csharp
// In EpisodeIdentificationOrchestrator pipeline:
// 1. DiscDbProvider (existing)
// 2. FileBotProvider (existing)
// 3. TmdbEpisodeProvider (NEW — searches TV show, fetches season catalog, maps by position)
// 4. Fallback (existing)
```

---

## Priority 2: Title Normalization for TMDB Search

**Impact:** High — Improves search accuracy  
**Effort:** Low (2-3 days)  
**Risk:** Low

### Problem
Disc info titles are noisy (e.g., "Season 3 Disc 2: Episodes 13-18"). Searching this directly against TMDB yields poor results. MKV Auto normalizes titles before search.

### Solution
Create a `TitleNormalizer` service:

```csharp
public class TitleNormalizer
{
    public TitleNormalizationResult Normalize(string rawTitle)
    {
        // Extract season hints: "Season 3" → season=3
        // Extract disc hints: "Disc 2" → disc=2
        // Extract edition hints: "Blu-Ray" → edition=bluray
        // Strip hints from query
        // Normalize punctuation: " - " → " "
        // Collapse whitespace
        // Return: Query, Season, Disc, Edition
    }
}
```

### Implementation Steps
1. Create `TitleNormalizer` class in `ArmRipper.Core/Services/`
2. Implement regex-based extraction for season, disc, edition patterns
3. Integrate into `TmdbService.SearchAsync()` before API call
4. Use extracted hints for type filtering (season hint → prefer TV search)

---

## Priority 3: Auto-Fill Season/Episode from Position

**Impact:** Medium — Reduces manual labeling  
**Effort:** Low (2-3 days)  
**Risk:** Low

### Problem
After TMDB season catalog is fetched, episode numbers must be manually assigned to each track. MKV Auto auto-fills based on track position.

### Solution
Extend `TrackMapperService` with position-based auto-fill:

```csharp
// After TMDB season catalog fetched and user selects episodes:
public void AutoFillEpisodesFromPosition(
    IReadOnlyList<Track> tracks,
    IReadOnlyList<TmdbEpisode> episodes,
    int startOffset = 0)
{
    // Track N → Episode offset+N
    // Skip tracks already mapped by DiscDb
    // Handle multi-part: if track duration ≈ 2× episode duration, assign two episodes
}
```

### Implementation Steps
1. Add `AutoFillEpisodesFromPosition` method to `TrackMapperService`
2. Call after TMDB catalog fetched and episodes selected
3. Skip tracks with existing `EpisodeNumber` (DiscDb-mapped)
4. Allow user override for multi-part episodes

---

## Priority 4: TMDB Auto-Suggestion on Scan

**Impact:** Medium — Reduces labeling time  
**Effort:** Low (2-3 days)  
**Risk:** Low

### Problem
Users must manually search TMDB for every disc. MKV Auto automatically searches TMDB on scan completion and presents the best match.

### Solution
Add post-scan TMDB search:

```csharp
// In Conductor.RunAsync() after IdentifyAsync:
if (job.VideoType == "tv" && job.TmdbId == null)
{
    var suggestion = await _tmdbService.SearchAndSuggestAsync(
        job.Title, job.Year, job.VideoType, ct);
    if (suggestion != null)
    {
        job.TmdbSuggestion = suggestion; // Store for UI
    }
}
```

### Implementation Steps
1. Add `TmdbSuggestion` property to `Job` model
2. Create `SearchAndSuggestAsync` in `TmdbService` (search + score + rank)
3. Add EF migration for `TmdbSuggestion` column
4. Display suggestion in job detail UI (FilmLabel component)

---

## Priority 5: Boxset Model & Collection Management

**Impact:** High — Enables collection tracking  
**Effort:** High (3-4 weeks)  
**Risk:** Medium

### Problem
ARM Sharp has no concept of boxsets, releases, or collection management. MKV Auto has full CRUD for these entities.

### Solution
Add three-tier collection model:

```
Movie (TMDB-backed)
    └── Release (edition, year, cover art)
        └── Disc (content hash, scan data)
```

### Implementation Steps

**Phase 1: Models & Migrations**
1. Create `Movie` entity: Id, TmdbId, Title, Year, PosterUrl
2. Create `Release` entity: Id, MovieId, Title, Year, Type, CoverUrl, DiscCount
3. Create `Boxset` entity: Id, Name, Slug, Year, Upc, Asin, CoverUrl
4. Create `BoxsetRelease` junction: BoxsetId, ReleaseId
5. Add `ReleaseId` to `Job` model (optional foreign key)
6. Create EF migration

**Phase 2: Services**
1. `MovieService` — CRUD, TMDB sync, poster download
2. `ReleaseService` — CRUD, link to movie/boxset
3. `BoxsetService` — CRUD, link/unlink releases

**Phase 3: UI Components**
1. `MovieSelectorComponent` — TMDB search, auto-suggest, manual entry
2. `ReleaseSelectorComponent` — Select/create release for job
3. `BoxsetSelectorComponent` — Search/create/edit boxsets
4. Library page — Compact disc-drawer title cards

---

## Priority 6: Duplicate Group Detection

**Impact:** Medium — Handles obfuscated discs  
**Effort:** Medium (1-2 weeks)  
**Risk:** Low

### Problem
ARM Sharp doesn't handle obfuscated Blu-ray playlists (Lions Gate, Avatar UHD). MKV Auto detects duplicate segment groups and lets users select the canonical playlist.

### Solution
Implement segment map grouping:

```csharp
public class DuplicateGroupDetector
{
    public IReadOnlyList<DuplicateGroup> DetectGroups(
        IReadOnlyList<MakeMkvTitle> titles)
    {
        // Group titles by sorted segment map
        // Return groups with primary (first) and siblings
    }
}

public class DuplicateGroup
{
    public string GroupId { get; set; }
    public IReadOnlyList<MakeMkvTitle> Titles { get; set; }
    public MakeMkvTitle Primary { get; set; }
}
```

### Implementation Steps
1. Create `DuplicateGroupDetector` in `ArmRipper.Core/Services/`
2. Parse segment maps from MakeMKV scan results
3. Group by sorted segment map (same segments = duplicate)
4. Add `DuplicateGroupId` and `IsPrimary` to `Track` model
5. Create duplicate group UI (expand/collapse, make primary)
6. Integrate into label step of job workflow

---

## Priority 7: Configurable Path Templates

**Impact:** Medium — Customizes output naming  
**Effort:** Low (3-5 days)  
**Risk:** Low

### Problem
ARM Sharp's output naming is hardcoded. MKV Auto has configurable templates for Plex/Jellyfin compatibility.

### Solution
Add path template configuration:

```csharp
public class PathTemplateService
{
    public string ResolveTemplate(string template, Job job, Track track)
    {
        // Variables: {type_dir}, {title}, {year}, {season}, {episode},
        // {resolution}, {edition}, {format}, {release}
        // Movie: "{type_dir}/{title} ({year})/{title}.{resolution}.mkv"
        // Series: "{type_dir}/{title}/Season {season}/{title} - s{season:02}e{episode:02} - {episode_title}.mkv"
    }
}
```

### Implementation Steps
1. Create `PathTemplate` model with `MovieTemplate` and `TvTemplate` fields
2. Add to settings/configuration
3. Implement `PathTemplateService` with variable substitution
4. Integrate into output naming logic
5. Add template editor to settings UI

---

## Priority 8: Transfer System

**Impact:** Medium — Enables file delivery  
**Effort:** Medium (1-2 weeks)  
**Risk:** Medium

### Problem
ARM Sharp doesn't have a transfer system. MKV Auto supports local, SMB, rsync, and NFS transfer.

### Solution
Implement transfer abstraction:

```csharp
public interface ITransferProvider
{
    Task<TransferResult> TransferAsync(
        string sourcePath, string destinationPath, 
        CancellationToken ct = default);
}

// Implementations:
public class LocalTransferProvider : ITransferProvider { }
public class SmbTransferProvider : ITransferProvider { }
public class RsyncTransferProvider : ITransferProvider { }
public class NfsTransferProvider : ITransferProvider { }
```

### Implementation Steps
1. Create `ITransferProvider` interface
2. Implement `LocalTransferProvider` (file copy)
3. Implement `SmbTransferProvider` (SMB client)
4. Implement `RsyncTransferProvider` (rsync CLI)
5. Create `TransferService` orchestrator
6. Add transfer settings to configuration
7. Add transfer step to job workflow

---

## Implementation Roadmap

### Phase 1: TV Workflow (2-3 weeks)
- [ ] Title normalization service
- [ ] TMDB season/episode catalog API
- [ ] Auto-fill from position
- [ ] Auto-suggestion on scan
- [ ] Season/episode selection UI

### Phase 2: Collection Management (3-4 weeks)
- [ ] Movie/Release/Boxset models
- [ ] Database migrations
- [ ] BoxsetSelector component
- [ ] MovieSelector component
- [ ] Release linking

### Phase 3: Output & Transfer (2-3 weeks)
- [ ] Path templates
- [ ] Transfer providers (local, SMB, rsync, NFS)
- [ ] Transfer settings
- [ ] Auto-rip on insert

### Phase 4: Polish (1-2 weeks)
- [ ] Duplicate group detection
- [ ] Library page
- [ ] Settings UI
- [ ] Documentation updates

---

## Success Metrics

### TV Series Workflow
- [ ] User can identify TV series without DiscDb (TMDB search)
- [ ] User can select season and episodes from TMDB catalog
- [ ] Episode numbers auto-fill from track position
- [ ] Auto-suggestion reduces labeling time by 50%

### Collection Management
- [ ] User can create and manage boxsets
- [ ] User can link releases to boxsets
- [ ] Library page shows all ripped content
- [ ] "Already in Library" detection works

### Output & Transfer
- [ ] User can configure path templates
- [ ] Files transfer to Plex/Jellyfin structure
- [ ] Multiple transfer modes work (local, SMB, rsync)

---

## Risk Mitigation

### TMDB API Rate Limits
- Implement caching for TMDB responses
- Use LRU cache for season catalogs
- Handle 429 Too Many Requests gracefully

### Database Migration
- Use EF Core migrations for schema changes
- Test migrations on staging before production
- Provide rollback scripts

### Backward Compatibility
- New features should be opt-in where possible
- Existing jobs should continue to work
- No breaking changes to existing API contracts

---

## References

- [MKV Auto Feature Set](./mkv-auto-features.md)
- [TV Series Identification Deep Dive](./tv-series-identification.md)
- [Comparison Matrix](./comparison-matrix.md)
- [ARM Sharp Architecture](../ARCHITECTURE.md)
- [TV Series Strategy Design](../DESIGN-TV-Series-Strategy.md)
