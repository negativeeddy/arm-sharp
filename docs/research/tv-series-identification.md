# TV Series & Box Set Identification — Deep Dive

**Focus:** How MKV Auto handles TV box sets and series for identification, and how it compares to ARM Sharp's current approach.

---

## 1. MKV Auto — TV Series Identification Flow

### 1.1 End-to-End Pipeline

```
Disc Insert
    ↓
Scan (MakeMKV info)
    ↓
Compute Content Hash → TheDiscDB Lookup
    ↓                          ↓
  HIT                        MISS
    ↓                          ↓
Enrich with DiscDB metadata    User selects TMDB URL or searches
    ↓                          ↓
Track metadata overlaid        MovieSelector / FilmLabel selects
    ↓                          ↓
Label Step (TitleLabel)        Label Step (TitleLabel)
    ↓                          ↓
Rip → Post-process → Transfer
```

### 1.2 TheDiscDB Integration

When a disc is scanned, MKV Auto:

1. **Computes content hash** from `BDMV/STREAM/*.m2ts` file sizes (sorted order)
2. **Queries TheDiscDB GraphQL API** (`queryByContentHash`)
3. **Returns** series name, season number, release year, track mappings
4. **Enriches scan results** with DiscDB metadata per track:
   - `type`: "episode", "extra", "trailer", etc.
   - `season`: Season number
   - `episode`: Episode number
   - `title`: Episode title
   - `episode_name`: Human-readable episode name
   - `description`: Episode synopsis

**Key point:** DiscDB enrichment is *overlay only* — it doesn't overwrite structural scan data (source files, segment maps, stream info). The local scan remains canonical.

### 1.3 TMDB Search & Episode Catalog

When DiscDB misses or user needs to manually identify:

**A. Title Normalization** (`normalize_title` in `tmdb_client.py`):
```python
# Input: "Season 3 Disc 2: Episodes 13-18"
# Output: query="episodes", hints={"season": 3, "disc": 2}
# Strips: season/disc numbers, edition tokens, punctuation
```

**B. Search** (`search_title`):
1. Search TMDB `/search/multi` with normalized query
2. Filter by type (movie vs tv)
3. Score candidates: title_overlap (0.75) + popularity (0.15) + year_proximity (0.10)
4. Return ranked `TmdbCandidate` list

**C. Episode Catalog** (`get_tv_season_episodes`):
1. Once user selects a TV show, fetch show details (`get_tv_details`)
2. Determine number of seasons (`data.get("number_of_seasons", 1)`)
3. Fetch episode list for each season (`/tv/{id}/season/{n}`)
4. Return `TmdbEpisode` list with: season, episode, name, overview, air_date, runtime, still_url

**D. Season Selection UI:**
1. Dropdown with "Season 1" through "Season N"
2. Episode checkboxes with episode numbers
3. Optional "Auto-fill season numbers" checkbox
4. Save selected season/episode for each track

### 1.4 MovieSelector Component

The `MovieSelectorComponent` provides the search-as-you-type experience:

**Search Modes:**
- **Search (TMDB)**: Backend `POST /movies/tmdb-search` → returns `TmdbSearchCandidate[]`
- **Backend**: Direct database lookup
- **TMDB URL**: User pastes URL, backend scrapes metadata

**Search Flow:**
1. User types search query
2. Backend queries TMDB API with normalized title
3. Results displayed with: title, year, poster, score, TMDB type badge (movie/TV)
4. User selects a result
5. Backend stores TMDB suggestion on disc (for later use)

### 1.5 FilmLabel Component

For movie/series identification:

**Modes:**
1. **TMDB URL**: Paste URL → auto-extract metadata
2. **Search**: `POST /api/discs/{id}/discdb-search` → `POST /movies/tmdb-search`
3. **Manual**: Enter title, year, cover URL manually

**Auto-suggestion Flow (v2+):**
1. On scan completion, backend automatically searches TMDB
2. `POST /api/movies/tmdb-search` with normalized disc title
3. Best candidate stored as `disc_info.tmdb_suggestion`
4. On FilmLabel display, suggestion shown as quick-select
5. User confirms or overrides

### 1.6 TitleLabel Component

The labeling step handles track-level metadata:

**For TV Series:**
- Season/episode numbers displayed as chips
- "MainMovie" type badge for primary content
- "Episode" type badge for individual episodes
- Duplicate group detection (same segment map = group)
- Preview clip per title (streaming from backend)

**Duplicate Group UX:**
- Group header: "Same as" link to primary title
- Expand/collapse grouped titles
- "Make primary" / "Ungroup" actions
- Type inheritance from primary to siblings

---

## 2. ARM Sharp — TV Series Identification Flow

### 2.1 Current Architecture

```
Disc Insert
    ↓
Mount (Windows: MountedImageMounter; Linux: manual)
    ↓
Detect Video Type (TS/MTS → TS; VOB → DVD; M2TS → Blu-ray)
    ↓
IdentifyService.IdentifyAsync()
    ↓
┌─────────────────────────────────────┐
│ 1. QueryDiscDbAsync()              │ ← Content hash + track mapping
│ 2. ComputeOvidFingerprintAsync()   │ ← Structural fingerprint (DVD)
│ 3. QueryOvidApiAsync()             │ ← OVID API lookup
│ 4. RunFallbackTitleLookupAsync()   │ ← Title search (if all else fails)
└─────────────────────────────────────┘
    ↓
TrackMapperService.MapTracksAsync()
    ↓
Conductor.RunAsync()
    ↓
Rip → Transcode → Done
```

### 2.2 IdentifyService

**DiscDb Lookup:**
1. Compute content hash from disc structure
2. Query TheDiscDB API with hash
3. If hit: set `Job.Title`, `Job.Year`, `Job.VideoType` (movie/tv)
4. If miss: proceed to OVID or fallback

**OVID (Obfuscated Video Identifier):**
1. Compute structural fingerprint (file sizes + folder layout)
2. Query OVID API
3. If hit: set title/year/type

**Fallback:**
- Search by disc title (basic text search)
- User manual entry

### 2.3 TrackMapperService

Matches DiscDb titles to MakeMKV tracks:

```csharp
// Matching criteria with weights:
// Index:     0.60 (track position)
// Duration:  0.30 (runtime matching)
// Size:      0.10 (file size)
```

**Per-track metadata set:**
- `Track.EpisodeNumber` — From DiscDb
- `Track.EpisodeTitle` — From DiscDb
- `Track.ContentType` — "MainMovie", "Extra", etc.
- `Track.TrackSeasonNumber` — From DiscDb

### 2.4 Job Model

```csharp
public class Job {
    public string VideoType { get; set; }  // "movie" or "tv"
    public int? SeriesTmdbId { get; set; }
    public int? SeasonNumber { get; set; }
    // ...
}
```

### 2.5 Track Model

```csharp
public class Track {
    public int? EpisodeNumber { get; set; }
    public string? EpisodeTitle { get; set; }
    public string? ContentType { get; set; }
    public int? TrackSeasonNumber { get; set; }
    // ...
}
```

---

## 3. Gap Analysis: TV Series Identification

### 3.1 What MKV Auto Has That ARM Sharp Lacks

| Feature | MKV Auto | ARM Sharp | Gap |
|---------|----------|-----------|-----|
| TMDB API search by title | ✅ Full client with normalization | ❌ Basic `SearchMovieAsync` (movie then TV) | ARM Sharp needs proper TV search with season/episode catalogs |
| TMDB season/episode catalogs | ✅ `get_tv_season_episodes()` | ❌ Not implemented | Critical for TV workflow |
| Auto-fill season/episode from track position | ✅ DiscDB enrichment + manual | ⚠️ TrackMapperService sets from DiscDb only | ARM Sharp only fills from DiscDb, no TMDB fallback |
| Boxset management | ✅ Full boxset model + UI | ❌ No boxset concept | Need boxset model, service, UI |
| Release linking (movie → release → disc) | ✅ Three-tier model | ⚠️ Job links to Title directly | ARM Sharp's model is flatter |
| Duplicate group detection | ✅ Segment map grouping | ❌ Not implemented | Needed for obfuscated discs |
| Path templates for output naming | ✅ Configurable templates | ⚠️ Hardcoded in naming service | Need configurable templates |
| Auto-rip on insert | ✅ DiscDB-aware auto-start | ❌ Not implemented | Convenience feature |
| DiscDB enrichment overlay | ✅ Metadata overlaid on scan | ⚠️ TrackMapperService matches separately | Different approach, similar result |
| Search-as-you-type | ✅ Debounced backend search | ⚠️ Basic search in UI | Need enhanced search UX |
| TMDB auto-suggestion on scan | ✅ Automatic TMDB search post-scan | ❌ Not implemented | Reduces manual labeling |
| Preview clips per title | ✅ Streaming preview | ❌ Not implemented | Nice-to-have for verification |
| Title normalization for search | ✅ Season/disc/edition extraction | ❌ Not implemented | Needed for reliable TMDB search |

### 3.2 What ARM Sharp Has That MKV Auto Lacks

| Feature | ARM Sharp | MKV Auto | Gap |
|---------|-----------|----------|-----|
| OVID fingerprinting | ✅ `ComputeOvidFingerprintAsync` | ❌ Not implemented | ARM Sharp has alternative ID method |
| SignalR real-time updates | ✅ Built into architecture | ⚠️ WebSocket but different pattern | ARM Sharp's pattern is more integrated |
| Provider pipeline architecture | ✅ `EpisodeIdentificationOrchestrator` design | ❌ Monolithic labeling | ARM Sharp has better abstraction (design doc) |
| Dependency injection patterns | ✅ Full DI throughout | ⚠️ Some DI, some manual | ARM Sharp is more testable |
| .NET ecosystem integration | ✅ Native | ❌ Python | Different stacks, not comparable |
| SQLite (portable) | ✅ | ❌ PostgreSQL (heavier) | ARM Sharp is simpler to deploy |
| Multi-provider metadata | ✅ TMDB, OMDB, TVDB, DiscDb, OVID | ⚠️ TMDB + DiscDB only | ARM Sharp has richer provider ecosystem |

### 3.3 TV Series Identification — Detailed Comparison

#### TheDiscDB Usage

**MKV Auto:**
- Content hash → GraphQL query
- Returns: series name, season, track mappings
- Enrichment overlay: adds metadata to scan rows without overwriting
- Contribution: users can contribute disc data back to TheDiscDB

**ARM Sharp:**
- Content hash → REST API query
- Returns: title, year, video type, track list
- TrackMapperService: matches DiscDb titles to MakeMKV tracks by index/duration/size
- Cache: Redis-based caching of DiscDb responses

**Assessment:** Both use TheDiscDB effectively. MKV Auto's enrichment overlay is cleaner (doesn't need separate matching), but ARM Sharp's TrackMapperService provides weighted matching which handles edge cases better.

#### TMDB Integration

**MKV Auto:**
- Full v3 API client with: search, season episodes, TV details
- Title normalization for noisy disc info
- Candidate scoring with popularity + year proximity
- Auto-suggestion on scan completion
- Season/episode catalog for manual selection

**ARM Sharp:**
- Basic `SearchMovieAsync` (searches movie then TV)
- `TmdbProvider` for episode identification (searches TV show, gets season, maps by position)
- No title normalization
- No auto-suggestion
- No candidate scoring

**Assessment:** MKV Auto's TMDB integration is significantly more mature. ARM Sharp's `TmdbProvider` has the right idea but lacks the normalization, scoring, and auto-fill features.

#### Episode Assignment

**MKV Auto:**
1. DiscDB provides episode numbers for matched tracks
2. For unmatched tracks: user selects from TMDB season/episode catalog
3. Auto-fill: position-based mapping from TMDB catalog
4. Manual override: user can change any episode number
5. Duplicate groups: tracks with same segments share metadata

**ARM Sharp:**
1. DiscDb provides episode numbers via TrackMapperService
2. For unmatched tracks: user manual entry only
3. No TMDB fallback for episode assignment
4. No auto-fill from position
5. No duplicate group concept

**Assessment:** ARM Sharp's episode assignment is DiscDb-dependent. When DiscDb misses, the user must manually enter all episode info. MKV Auto's TMDB catalog integration provides a much better fallback.

---

## 4. Box Set Handling — Detailed Comparison

### 4.1 MKV Auto Boxset Model

```
Movie (TMDB ID)
    └── Release (edition, year, cover art)
        └── Disc (content hash, scan data)
            └── Title (per-track metadata)
```

**Boxset:**
- Collection of releases
- Has: name, slug, year, UPC, ASIN, cover art
- Linkable to multiple releases
- Tracks completeness (missing required fields)

**Release:**
- Specific edition of a movie
- Has: title, year, type, cover art, disc count
- Links to: movie, boxset (optional)
- Has: finalize_state, transfer_state

**Movie:**
- TMDB-backed entity
- Has: title, year, TMDB ID, TMDB type, poster
- Links to: multiple releases

### 4.2 ARM Sharp Model (Current)

```
Job (single disc)
    └── Track (per-track metadata)
        └── VideoFile (output file)
```

**Job:**
- Represents a single disc
- Has: Title, Year, VideoType (movie/tv)
- Links to: single title

**No boxset or release concept exists.**

### 4.3 What ARM Sharp Needs for Box Sets

1. **Movie model** — TMDB-backed entity with poster, year, type
2. **Release model** — Specific edition linking to movie
3. **Boxset model** — Collection of releases
4. **Job → Release linking** — Job references a release, not directly a movie
5. **UI components** — BoxsetSelector, MovieSelector, ReleaseSelector
6. **TMDB integration** — Search, auto-suggest, poster download

---

## 5. Recommendations for TV Series Improvements

### 5.1 Priority 1: TMDB Season/Episode Catalogs

**Impact:** High — Enables TV series workflow without DiscDb  
**Effort:** Medium — Backend service + UI component

**Changes needed:**
1. Extend `TmdbService` with `GetTvSeasonEpisodesAsync(int tvId, int seasonNumber)`
2. Add `GetTvDetailsAsync(int tvId)` for season count
3. Create `TmdbEpisodeProvider` implementing `IEpisodeIdentificationProvider`
4. Add season/episode selection UI component
5. Integrate into `EpisodeIdentificationOrchestrator` pipeline

### 5.2 Priority 2: Title Normalization for TMDB Search

**Impact:** Medium — Improves TMDB search accuracy  
**Effort:** Low — Pure logic, no UI changes

**Changes needed:**
1. Create `TitleNormalizer` service with season/disc/edition extraction
2. Integrate into `TmdbService.SearchAsync()` before API call
3. Use extracted hints for type filtering (season hint → search TV)

### 5.3 Priority 3: Auto-Fill Season/Episode from Position

**Impact:** Medium — Reduces manual labeling  
**Effort:** Low — Extension of existing TrackMapperService

**Changes needed:**
1. After TMDB season catalog fetched, auto-assign episode numbers by position
2. Track N → Episode offset+N (where offset is first unassigned episode)
3. Allow user override for multi-part episodes

### 5.4 Priority 4: Boxset Model & UI

**Impact:** High — Enables collection management  
**Effort:** High — New models, services, UI

**Changes needed:**
1. Add `Movie`, `Release`, `Boxset` entities
2. Create migration scripts
3. Build `BoxsetSelectorComponent`
4. Build `MovieSelectorComponent` with TMDB search
5. Link Jobs to Releases instead of directly to Movies

### 5.5 Priority 5: Duplicate Group Detection

**Impact:** Medium — Helps with obfuscated discs  
**Effort:** Medium — Algorithm + UI

**Changes needed:**
1. Parse segment maps from MakeMKV scan results
2. Group titles by sorted segment map
3. Add duplicate group UI to job detail page
4. Allow "Make primary" / "Ignore group" actions

---

## 6. Implementation Roadmap

### Phase 1: TMDB Enhancement (1-2 weeks)
- [ ] Title normalization service
- [ ] TMDB season/episode catalog API
- [ ] Auto-suggestion on scan completion
- [ ] Season/episode selection UI

### Phase 2: TV Workflow Polish (1-2 weeks)
- [ ] Auto-fill from TMDB position
- [ ] Enhanced TMDB search with scoring
- [ ] Duplicate group detection (backend)
- [ ] Duplicate group UI

### Phase 3: Collection Management (3-4 weeks)
- [ ] Movie/Release/Boxset models
- [ ] Database migrations
- [ ] BoxsetSelector component
- [ ] MovieSelector component with TMDB search
- [ ] Release linking in job workflow

### Phase 4: Path Templates & Output (1-2 weeks)
- [ ] Configurable path templates
- [ ] Plex/Jellyfin output awareness
- [ ] Transfer system enhancements
- [ ] Auto-rip on insert
