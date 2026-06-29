# TheDiscDb Integration — Architecture & Process

> **Status:** Implemented (committed to `origin/feature/discdb-integration`)
> **Last updated:** 2025-07-17

## 1. Overview

This document describes how [TheDiscDb](https://thediscdb.com/) — a community-curated database of physical disc metadata — is integrated into ARM Sharp. TheDiscDb provides precise identification of video discs (DVD, Blu-ray, 4K UHD) by content hash, enabling:

- **Accurate movie/series metadata** (title, year, type, poster) during the identify stage
- **Track-level episode mapping** for TV season discs (episode number, title, content type)
- **Named output files** (`S01E01 - Pilot.mkv` instead of `title_00.mkv`)
- **Content-type-aware extras routing** (Trailers/, Featurettes/, Deleted Scenes/) by media server convention

---

## 2. Architecture

### 2.1 Service Diagram

```
┌──────────────────────────────────────────────────────────────────┐
│                        IdentifyService                           │
│  (called during identify stage, disc mounted and type detected)   │
│                                                                   │
│  1. Compute DiscDb hash from mounted disc filesystem             │
│     └─→ DiscDbHashService.ComputeHashAsync()                     │
│  2. Look up hash in local cache                                  │
│     └─→ DiscDbMappingService.GetCachedMappingAsync()             │
│  3. On miss, query TheDiscDb GraphQL API                         │
│     └─→ DiscDbQueryService.QueryByHashAsync()                    │
│  4. If match found, populate job-level metadata                  │
│     (title, year, video type, poster URL)                        │
│  5. Cache the result for future re-rips                          │
│     └─→ DiscDbMappingService.SaveMappingAsync()                  │
└──────────────────────────────────────────────────────────────────┘

┌──────────────────────────────────────────────────────────────────┐
│                    ArmRipperService                              │
│  (called during rip stage, after MakeMKV track info collected)   │
│                                                                   │
│  1. Pass infoMinLength=0 to MakeMKV so ALL tracks discovered     │
│  2. Load cached DiscDb mapping for this job's hash               │
│  3. Run TrackMapperService.MapTracksAsync() to match DiscDb      │
│     titles to MakeMKV tracks (by index, duration, file size)     │
│  4. Promote short DiscDb-matched tracks to Process=true          │
│  5. During rip, use minLength=0 for promoted tracks so MakeMKV   │
│     doesn't filter them out                                      │
│  6. During file move, use episode metadata for naming            │
└──────────────────────────────────────────────────────────────────┘
```

### 2.2 Service Inventory

| Service | File | Role | Lifetime |
|---------|------|------|----------|
| `DiscDbHashService` | `DiscDbHashService.cs` | Computes MD5 content hash from file sizes on the mounted disc | Scoped |
| `DiscDbQueryService` | `DiscDbQueryService.cs` | Queries TheDiscDb GraphQL API by content hash | Scoped |
| `DiscDbMappingService` | `DiscDbMappingService.cs` | Caches/retrieves DiscDb results in local SQLite DB | Scoped |
| `TrackMapperService` | `TrackMapperService.cs` | Matches DiscDb titles to MakeMKV tracks with weighted confidence | Scoped |

All four are registered via DI in both `ArmRipper.Cli/Program.cs` and `ArmRipper.WebUi/Program.cs`.

---

## 3. Pipeline Integration Points

### 3.1 Identify Stage — `IdentifyService.IdentifyAsync()`

The DiscDb lookup is inserted **after disc type detection** and the existing `GetVideoTitle`/metadata fetch, but **before** CRC64 fingerprint computation.

**Flow:**

1. If disc is DVD or Blu-ray and `DiscDbEnabled` is true:
   - Call `DiscDbHashService.ComputeHashAsync()` with the mount point and disc type
   - Store the resulting hash in `Job.DiscDbHash`
   - Check cache via `DiscDbMappingService.GetCachedMappingAsync()`
   - On cache miss, call `DiscDbQueryService.QueryByHashAsync()` and save to cache
   - If a mapping is found:
     - Map DiscDb `"Series"` type → `job.VideoType = "tv"`
     - Fill `job.Year` if not already set by other metadata sources
     - Fill `job.PosterUrl` from DiscDb's `imageUrl` (prefixed with `https://thediscdb.com/images/`) if OMDB returned `"N/A"`
2. Continue with existing fingerprint computation and unmount

**Key detail:** TheDiscDb identification runs *alongside* existing metadata sources (OMDB, TMDB). DiscDb data fills gaps rather than replacing other sources. The poster specifically checks for OMDB's `"N/A"` sentinel value.

### 3.2 Rip Stage — `ArmRipperService.PrepareTranscodeInputPathAsync()`

The DiscDb track mapping is inserted **after MakeMKV track info is collected and saved to the database**, but **before the rip loop begins**.

**Flow:**

1. When `DiscDbEnabled` is true, pass `infoMinLength = 0` to `MakeMkvService.GetTrackInfoWithCacheAsync()` — this makes MakeMKV report **all** tracks (including short extras) instead of filtering by `--minlength`
2. After saving tracks to the DB and setting `Process` flags by length, load the cached DiscDb mapping for this job's hash
3. Call `TrackMapperService.MapTracksAsync()` which matches DiscDb titles to MakeMKV tracks and populates `EpisodeNumber`, `EpisodeTitle`, `ContentType`, `TrackSeasonNumber`, and `DiscDbItemSlug` on each matched track
4. **Promotion:** Any track that received an `EpisodeTitle` from DiscDb but was below the configured `MinLength` gets its `Process` flag set to `true`. This ensures short extras (e.g., 30-second featurettes) are ripped even though they're below the normal minimum length threshold.
5. During the rip loop, DiscDb-promoted tracks use `minLength = 0` when calling `MakeMkvService.RipTrackAsync()` — this bypasses MakeMKV's `--minlength` filter so the short track is actually output

**MainFeature mode interaction:**
- When `MainFeature = true`: only the longest track is ripped. DiscDb metadata is still used for job-level fields (poster, title, year) but promoted extras are **not** ripped.
- When `MainFeature = false`: all eligible tracks are ripped. If any track has an `EpisodeTitle` (DiscDb-matched), the rip loop iterates individually per track instead of using the `RipAllTitlesAsync` fast path.

### 3.3 File Move Stage — `ArmRipperService.MoveFiles()`

The `MoveFiles` method checks track metadata to determine naming:

1. **TV episode naming:** If the track has `EpisodeNumber` and video type is `"series"` or `"tv"`: uses `S{season:D2}E{episode:D2} - {EpisodeTitle}.{ext}` naming in the job output directory
2. **Content-type extras routing:** If the track has a non-null, non-`"main"`, non-`"unknown"` `ContentType`: routes to an extras subfolder based on `ExtrasSub` config (Plex-style type-specific folders vs Jellyfin-style single `Extras/` folder) and uses `EpisodeTitle` as the filename
3. **Main feature suffix:** If the track is the main feature and has an `EpisodeTitle` that differs from the video title: appends it as a suffix (e.g., `"Freaky Friday - Widescreen.mkv"`)
4. **Fallback:** For non-main features without DiscDb mapping: falls back to the existing behavior (uses original filename)

The `SanitizeFileName()` helper strips characters invalid in filenames via `Path.GetInvalidFileNameChars()`.

---

## 4. Data Models

### 4.1 New Database Entities

**`DiscDbMapping`** — Caches TheDiscDb results locally:
- `ContentHash` (PK-style, string) — uppercase hex MD5 of file sizes
- `MediaSlug`, `MediaTitle`, `MediaYear`, `MediaType` — job-level metadata
- `ImageUrl` — relative poster path from DiscDb
- `TrackMappingsJson` — JSON-serialized flattened track data for re-rip reconstruction
- `LastUsedAt`, `CreatedAt` — timestamps

**Column additions to `Job`:**
- `DiscDbHash` (string?) — the content hash computed during identify
- `SeriesTmdbId` (int?) — TMDB series ID for TV shows
- `SeasonNumber` (int?) — auto-detected or user-set season number

**Column additions to `Track`:**
- `EpisodeNumber` (int?) — episode number within season
- `EpisodeTitle` (string?) — episode name from DiscDb
- `ContentType` (string?) — "movie", "episode", "extra", "trailer", "commentary", etc.
- `TrackSeasonNumber` (int?) — season for this specific track (supports multi-season discs)
- `DiscDbItemSlug` (string?) — DiscDb item identifier

All new columns are **nullable** — zero migration impact for existing jobs.

### 4.2 DiscDb GraphQL DTOs

Defined in `DiscDbModels.cs`. Key response shape:

```
DiscDbGraphQlResponse
  └─ Data.MediaItems.Nodes[] → DiscDbMediaResult
       ├─ Id (int, from "id")
       ├─ Title
       ├─ Year (string, with FlexibleStringConverter — handles int or string)
       ├─ Slug, ImageUrl
       ├─ Type ("Movie" | "Series", via ItemTypeJsonConverter → "movie" | "series")
       └─ Releases[]
            ├─ Slug, Title
            └─ Discs[]
                 ├─ Index, Name, Format, Slug
                 └─ Titles[]
                      ├─ Index, Duration (via DurationJsonConverter — handles int or "H:MM:SS")
                      ├─ Size, SourceFile, SegmentMap
                      └─ Item { Title, Season, Episode, Type }
```

The response also includes an `Errors` property (`List<DiscDbGraphQlError>`) for logging GraphQL-level errors. The `Path` property in errors uses `List<JsonElement>` to handle mixed string/number path segments.

### 4.3 Cache Serialization

`DiscDbFlatTrack` is used for JSON serialization in `TrackMappingsJson`:
- `trackIndex` (int)
- `durationSeconds` (int?)
- `fileSize` (long?)
- `itemTitle`, `season`, `episode`, `itemType` (strings/ints)

These fields are annotated with `[JsonPropertyName]` to match the camelCase JSON keys used during serialization.

---

## 5. DiscDbHashService — Content Hash Computation

Computes an MD5 hash from the **sizes** of files on the disc (no file content is read).

### Algorithm

| Disc Type | Search Path | File Pattern | Sorting |
|-----------|------------|--------------|---------|
| DVD | `{mountPoint}/VIDEO_TS/` | `*` (all files) | By filename, ordinal |
| Blu-ray | `{mountPoint}/BDMV/STREAM/` | `*.m2ts` | By filename, ordinal |

For each file (sorted by name), its `Length` property is converted to 8 little-endian bytes and fed into `MD5.TransformBlock()`. After all files, `TransformFinalBlock` produces the final hash, returned as uppercase hex.

### Hash determinism

The hash is **deterministic** — same disc filesystem → same hash. This matches TheDiscDb's ImportBuddy implementation. Unit tests verify against known file listings.

### Error handling

- Unsupported disc type → return null, log debug
- Mount point or search path missing → return null, log warning
- No files found → return null, log warning
- IO errors → log error, return null

---

## 6. DiscDbQueryService — GraphQL API Client

### Endpoint

`POST https://thediscdb.com/graphql` (configurable via `ArmSettings.DiscDbApiBaseUrl`, defaults to this value)

### Authentication

**None required** for queries. No API key, token, or login needed — confirmed with TheDiscDb maintainer.

### GraphQL Query

The query uses TheDiscDb's canonical `GetDiscDetailByContentHash` with `some`/`eq` filtering:

```graphql
query GetDiscDetailByContentHash($hash: String) {
  mediaItems(where: {
    releases: { some: { discs: { some: { contentHash: { eq: $hash } } } } }
  }) {
    nodes {
      id, title, year, slug, imageUrl, type
      releases {
        slug, title
        discs(order: { index: ASC }) {
          index, name, format, slug
          titles(order: { index: ASC }) {
            index, duration, displaySize, sourceFile, size, segmentMap
            item { title, season, episode, type }
          }
        }
      }
    }
  }
}
```

### HTTP Client

- Named `HttpClient` (`"TheDiscDb"`) via `IHttpClientFactory`
- 10-second timeout
- Raw response content is read as string before deserialization to enable debug logging

### Response Handling

- Logs GraphQL errors (partial data scenario) as warnings
- Returns first matching `DiscDbMediaResult` from `nodes` array
- On "no match" (null result), logs raw response at Debug level

### Custom JSON Converters

Located in `DiscDbModels.cs`:

| Converter | Purpose |
|-----------|---------|
| `DurationJsonConverter` | Parses `duration` field that may be an integer (seconds) or string (`"H:MM:SS"`) |
| `FlexibleStringConverter` | Parses fields like `year` that may be an integer or string |
| `ItemTypeJsonConverter` | Normalizes `item.type` from PascalCase (`"MainMovie"`) to lowercase (`"main"`) |

---

## 7. TrackMapperService — Matching Algorithm

Matches TheDiscDb title metadata to MakeMKV-identified tracks using weighted signals.

### Signals & Weights

| Signal | Weight | How it works |
|--------|--------|-------------|
| Index match | 60% | If MakeMKV track number equals DiscDb title index, score = 1.0 |
| Duration match | 30% | Tolerance: ±5% or ±30s (whichever is larger). Score decreases linearly within tolerance. |
| File size match | 10% | Score = `max(0, 1 - (sizeDiff / maxSize) * 5)`. Acts as tiebreaker. |

### Matching Process

1. Flatten all titles from all releases/discs in the DiscDb result
2. For each DiscDb title, find the best-matching MakeMKV track from a mutable pool
3. A track is only matched if confidence ≥ 0.3 (minimum threshold)
4. Once matched, the track is **removed from the pool** to prevent duplicate claims
5. Matched tracks get `EpisodeNumber`, `EpisodeTitle`, `ContentType`, `TrackSeasonNumber`, and `DiscDbItemSlug` populated
6. Returns average confidence across all matches

### Thresholds

| Confidence | Behavior |
|-----------|----------|
| ≥ 0.7 | Considered a good match (auto-accept, no user intervention needed) |
| 0.3 – 0.69 | Match is applied but may be low quality |
| < 0.3 | Track is not matched |

The confidence value returned by `MapTracksAsync()` is logged but currently does not block processing — matches are always applied. The threshold is informative for debugging and future UI enhancement.

---

## 8. Configuration

### 8.1 Settings (`ArmSettings.cs`)

| Setting | Default | Purpose |
|---------|---------|---------|
| `DiscDbEnabled` | `true` | Master toggle for all DiscDb functionality |
| `DiscDbApiBaseUrl` | `"https://thediscdb.com/graphql"` | GraphQL endpoint URL |
| `DiscDbMinConfidence` | `0.7` | Minimum confidence for auto-accept (informational — not yet enforced in UI) |
| `DiscDbRequireConfirmation` | `false` | Reserved for future "require user confirmation" mode |

### 8.2 MinLength Interaction

The default `MinLength` across the application is **300** (seconds, i.e. 5 minutes):

- `ArmSettings.MinLength` defaults to `300`
- `appsettings.json` sets `MinLength: 300`
- `SettingsController` uses `MinLength ?? 300`
- When DiscDb is enabled, `infoMinLength = 0` is passed to MakeMKV's info command so ALL tracks are reported
- DiscDb-promoted tracks (those with an `EpisodeTitle` below `MinLength`) use `minLength = 0` during rip to bypass MakeMKV's `--minlength` filter

### 8.3 ExtrasSub Mode

The `ExtrasSub` setting controls how extras are organized:

| Value | Behavior |
|-------|----------|
| `null` / `""` | Plex-style: type-specific subfolders (Trailers/, Featurettes/, Deleted Scenes/, etc.) |
| `"jellyfin"` | Jellyfin-style: single `Extras/` folder for all supplementary content |

The mapping from `ContentType` to subfolder is in `ArmRipperService.GetExtrasSubFolder()`.

---

## 9. UI Changes

### 9.1 Job Detail — Colored Type Badges

The track table in `Jobs/JobDetail.cshtml` displays content type with color-coded badges:

- **`bg-success`** (green) — `"main"` content type and main feature tracks
- **`bg-primary`** (blue) — Known non-main types (extra, deleted_scene, trailer, etc.) that will be ripped, or untyped `Process=true` tracks
- **`bg-info text-white`** (light blue, white text) — Known non-main types that won't be ripped
- **`bg-secondary`** (gray) — Unrecognized/unmapped content types
- **`bg-danger bg-opacity-25 text-danger`** (light red) — Tracks with no content type that won't be ripped

### 9.2 Settings Page — DiscDb Cache

The Ripper tab in `Settings/Index.cshtml` includes a "Disc Database Cache" section with a button to clear cached DiscDb mappings.

### 9.3 Completed Page — Delete Folder

The `Completed/Index.cshtml` view includes a delete-folder button on directory divider rows:
- A confirmation modal shows file listing (≤10 files shown in full, >10 truncated with count)
- Endpoints: `CompletedController.DeleteFolderPreview` (GET) and `CompletedController.DeleteFolder` (POST)

### 9.4 No Dedicated DiscDb State

The design originally considered a `pending_discdb` job state for user confirmation of track mappings. This was **not implemented**. Instead, the existing `ManualWait` stage is used if user intervention is needed. Track mapping confidence is logged but currently non-blocking.

---

## 10. Fallback Strategy

DiscDb integration is **additive** — it never replaces the existing metadata pipeline:

| Failure Mode | Behavior |
|-------------|----------|
| Hash computation fails | Log warning, skip, existing metadata flow unaffected |
| API unreachable | Log warning, skip. Cached mappings still work for re-rips |
| API returns no match | Log at Debug level, continue with existing fallbacks |
| Track mapping finds no matches | Job-level metadata still used, tracks named with current convention |

---

## 11. What Wasn't Implemented

The following items from the original design were **deferred or not implemented** to keep the initial integration focused:

- **DiscDb submission / ImportBuddy hash log generation** — Users can manually submit discs to TheDiscDb via their GitHub PR workflow
- **Episode mapping edit page** (`/jobs/{id}/map-episodes`) — No drag-and-drop UI for manual track reassignment; mapping is fully automatic
- **`pending_discdb` job state** — No new state machine entry; ManualWait handles any needed pauses
- **Rate limiting / `Retry-After` handling** — TheDiscDb has no published rate limits; caching ensures minimal API calls
- **Multi-release disambiguation** — If multiple media items match the same hash, the first is used (rare in practice)

---

## 12. Testing

Located in `tests/ArmRipper.Core.Tests/`:

| Test File | Coverage |
|-----------|----------|
| `DiscDbHashServiceTests.cs` | Hash computation with known file listings, error cases |
| `DiscDbQueryServiceTests.cs` | Mock HTTP handler, response deserialization, error logging |
| `TrackMapperServiceTests.cs` | Index/duration/size matching, confidence calculation, dedup |
| `DiscDbMappingServiceTests.cs` | Cache save/restore, ImageUrl serialization, touch behavior |

Run with: `dotnet test tests/ArmRipper.Core.Tests`

---

## 13. Key Design Decisions

| Decision | Rationale |
|----------|-----------|
| **GraphQL over REST** | TheDiscDb only exposes GraphQL |
| **Raw HTTP over GraphQL client library** | Single query, no reason to add StrawberryShake |
| **Cache in SQLite (same DB)** | No additional infrastructure; one tiny table per unique disc |
| **Weighted matching over exact index** | Discs can have minor variations; graduated confidence avoids brittle yes/no |
| **Hash from file sizes only** | Matches TheDiscDb's ImportBuddy algorithm; fast (no content read) |
| **infoMinLength=0 when DiscDb enabled** | MakeMKV's `--minlength` would hide short extras; we handle filtering ourselves |
| **Separate mapper service** | Encapsulates matching algorithm; independently testable and swappable |

---

## Appendix: References

| Resource | URL |
|----------|-----|
| TheDiscDb website | https://thediscdb.com/ |
| GraphQL query reference | https://github.com/TheDiscDb/data/blob/main/tools/ImportBuddy/source/ImportBuddy/TheDiscDb.Client/GraphQL/Queries/GetDiscDetailByContentHash.graphql |
| Hash algorithm source | https://github.com/TheDiscDb/data/blob/main/tools/ImportBuddy/source/ImportBuddy/TheDiscDb.Core/DiscHash/HashingExtensions.cs |
| Disc file scanning | https://github.com/TheDiscDb/data/blob/main/tools/ImportBuddy/source/ImportBuddy/ImportBuddy/DiskContentHash.cs |
| DanForever's DiscRipper | https://github.com/DanForever/DiscRipper |
| Original design discussion | https://github.com/orgs/TheDiscDb/discussions/69 |
