# MKV Auto Release — Feature Set Analysis

**Repository:** `github.com/MKV-Auto/mkv-auto-release`  
**Stack:** Python 3 + FastAPI + Celery + PostgreSQL + Redis + Angular 17  
**Architecture:** Single-container deployment with embedded DB/Redis

---

## 1. Core Pipeline

MKV Auto implements a multi-stage pipeline with clear state transitions:

```
Disc Insert → Scan → Identify → Label → Rip → Post-process → Transfer
```

### Pipeline Stages

| Stage | Description | State Values |
|-------|-------------|--------------|
| Scan | MakeMKV info scan, DiscDB hash lookup | `pending`, `running`, `completed`, `failed` |
| Label | User labeling (movie/series selection, title assignment) | `pending`, `ready`, `running`, `completed`, `failed` |
| Rip | MakeMKV title extraction | `pending`, `ready`, `running`, `completed`, `failed` |
| Post-process | File rename, metadata, extras organization | `pending`, `ready`, `running`, `completed`, `failed` |
| Transfer | Move to Plex/Jellyfin share (local/SMB/rsync/NFS) | `pending`, `ready`, `running`, `completed`, `failed` |

### Key Design Decisions

- **Backend as source of truth**: PostgreSQL holds all state; frontend is stateless
- **Explicit workflows**: Clear state progression allows resume after failures
- **Recovery-friendly**: Jobs designed for restart without manual fix-up
- **Single container**: All services (API, workers, DB, Redis) in one Docker image

---

## 2. Disc Identification

### 2.1 Content Hash (TheDiscDB)

MKV Auto computes a content hash by scanning `BDMV/STREAM/*.m2ts` (Blu-ray) or `VIDEO_TS/*` (DVD) files and hashing file sizes in order. This hash is looked up against TheDiscDB API.

**Flow:**
1. Scan disc with MakeMKV (`makemkvcon info`)
2. Compute content hash from disc file structure
3. Query TheDiscDB GraphQL API with hash
4. If hit: pre-fill labels with DiscDB metadata
5. If miss: proceed to manual labeling

### 2.2 TMDB Integration

MKV Auto has two TMDB integration paths:

**A. API Client (`tmdb_client.py`)**
- Requires API key (optional, enhances experience)
- `normalize_title()` — Strips season/disc/edition hints, normalizes punctuation
- `search_title()` — Searches TMDB `/search/movie`, `/search/tv`, or `/search/multi`
- `get_tv_season_episodes()` — Fetches episode catalog for a season
- `get_tv_details()` — Gets show metadata (number_of_seasons)
- Candidate ranking: title overlap (0.75) + popularity (0.15) + year proximity (0.10)

**B. HTML Scraper (`tmdb_scraper.py`)**
- No API key required
- Parses TMDB public pages for title, year, poster
- Fallback when API key not configured

### 2.3 DiscDB Enrichment

When DiscDB returns track-level metadata, MKV Auto enriches scan results:

```python
_DISCDB_METADATA_KEYS = ("type", "season", "episode", "title", "description", "episode_name")
```

This overlays episode numbers, titles, and content types onto MakeMKV scan tracks without overwriting structural data (source_file, segment_map, streams, etc.).

---

## 3. TV Series Handling

### 3.1 Series Identification

MKV Auto identifies TV series through multiple paths:

1. **DiscDB hit**: Series name + season number from content hash lookup
2. **TMDB search-by-title**: Normalized disc info_title searched against TMDB
3. **TMDB URL paste**: User provides TMDB URL, app scrapes metadata
4. **Manual entry**: User types series name and selects from TMDB results

### 3.2 Season/Episode Assignment

For TV seasons, MKV Auto:

1. Fetches TMDB episode catalog for the identified season
2. Displays episodes in a selectable list
3. Auto-fills season/episode numbers based on track position
4. Allows manual override for multi-part episodes or specials

**TMDB Episode Data:**
```python
@dataclass(frozen=True)
class TmdbEpisode:
    season_number: int
    episode_number: int
    name: str
    overview: Optional[str]
    air_date: Optional[str]
    runtime: Optional[int]
    still_url: Optional[str]
```

### 3.3 Title Normalization

MKV Auto's `normalize_title()` handles noisy disc info titles:

```python
# Examples:
"Season 3 Disc 2" → query="disc 2", hints={"season": 3}
"S3E05 Pilot" → query="pilot", hints={"season": 3, "episode": 5}
"Blu-Ray Edition" → query="", hints={"edition": "bluray"}
```

**Normalization steps:**
1. Punctuation → whitespace (preserves apostrophes/ampersands)
2. Extract season/disc hints (strip from query)
3. Strip trailing edition tokens
4. Lowercase and collapse whitespace

---

## 4. Box Set Management

### 4.1 Data Model

MKV Auto has first-class box set support:

- **Movie**: A single title (movie or TV series)
- **Release**: A specific edition of a movie (e.g., "4K UHD", "Blu-ray")
- **Disc**: A physical disc linked to a release
- **Boxset**: A collection of releases (e.g., "Harry Potter 8-Film Collection")

### 4.2 Boxset Selector Component

The `BoxsetSelectorComponent` provides:

- List all boxsets with search-as-you-type
- Create new boxsets with name, year, UPC, ASIN, cover art
- Edit boxset metadata (inline form)
- Link/unlink releases to/from boxsets
- Visual indication of incomplete boxsets (missing required fields)

### 4.3 Release Linking

When a disc is scanned:
1. DiscDB lookup returns release metadata
2. User can create or select an existing release
3. Release is linked to a movie and optionally a boxset
4. Multiple discs can share a release (multi-disc sets)

---

## 5. Obfuscation Handling (Path A / Path B)

### 5.1 Duplicate Group Detection

MKV Auto detects obfuscated Blu-ray playlists (Lions Gate, Avatar UHD, etc.) by:

1. Parsing segment maps from MakeMKV scan results
2. Grouping titles by sorted segment map
3. Detecting when multiple playlists share the same segments

### 5.2 Path A: Segment Reorder

For heavily obfuscated discs:
1. Detect duplicate segment groups + projected rip > threshold (200 GB)
2. Show threshold modal to user
3. User picks "Find canonical" → exploratory rip of one playlist
4. Generate per-PlayItem previews using MPLS PlayItem durations
5. User drags previews into story order
6. Backend matches user order to on-disc playlists
7. Re-rip canonical playlist via selective rip set

### 5.3 Path B: Dedupe Groups

For less severe cases:
1. Group titles by sorted segment map
2. Mark one as primary (active), others as secondary (ignore)
3. User can change primary via "Make primary" button
4. m2ts clips inherit wrapping mpls's ignore decision

---

## 6. Transfer System

### 6.1 Transfer Modes

| Mode | Description |
|------|-------------|
| Local | Copy to local directory |
| rsync | Remote sync over SSH |
| SMB | Windows share via smbclient |
| NFS | Network file system mount |

### 6.2 Path Templates

Configurable output paths with variable substitution:

```python
# Movie template
"{type_dir}/{movie} ({year})/{title}.{resolution}.mkv"

# Series template
"{type_dir}/{movie}/Season {season}/{movie} - s{season:02}e{episode:02} - {title}.mkv"
```

**Variables:** `type_dir`, `movie`, `year`, `title`, `resolution`, `edition`, `season`, `episode`, `format`, `release`

### 6.3 Plex/Jellyfin Awareness

MKV Auto adapts output structure for the target media server:

- **Plex**: `Movies/Title (Year)/Title.mkv`, `Series/Show/Season XX/Show - sXXeXX - Episode.mkv`
- **Jellyfin**: Same structure but different extras subfolder naming

---

## 7. Auto-Rip on Insert

When `auto_rip_enabled` setting is on:

1. Disc scan completes
2. If DiscDB hit: automatically start rip (full auto)
3. If DiscDB miss: rip first, user labels after
4. If unhashed disc: skip (identity too weak)
5. If Path A threshold: show action-required notification

---

## 8. Notifications

### 8.1 Toast Notifications (WebSocket)

Real-time UI updates via WebSocket:
- `rip_start`, `rip_complete`, `rip_failed`
- `label_complete`, `postprocess_complete`
- `transfer_started`, `transfer_completed`
- `action_required`, `error_disk_space`

### 8.2 Discord Integration

Optional Discord webhook notifications:
- Configurable per notification level
- 24h deduplication window
- Rich formatting with emoji indicators

---

## 9. Library Management

### 9.1 Library Page

- Compact disc-drawer title cards with type-colored edges
- Auto-ignored junk hidden by default (one-click "show ignored")
- "Already in Library" banner on re-insert
- Search results show "In Library" chip on owned titles

### 9.2 Disc Drawer

- Virtual scroll for large disc collections
- Per-title type chips (MainMovie, Episode, Extra, etc.)
- Inline edit with display/edit mode toggle
- Duplicate group collapse/expand
- Preview player for ripped content

---

## 10. Multi-Drive Support

- Concurrent ripping from multiple drives
- Per-disc workflow contexts
- Drive-swap detection and notifications
- Stable identity tracking by device serial

---

## 11. Settings & Configuration

### 11.1 Settings Categories

| Category | Key Settings |
|----------|--------------|
| MakeMKV | Registration key, install version, beta updates |
| TMDB | API key, search enable/disable |
| Transfer | Destination mode, path template, conflict resolution |
| Notifications | Discord webhook, per-level channel preferences |
| Previews | Duration, max parallel, ffmpeg detection |
| Auto-rip | Enable/disable toggle |

### 11.2 Setup Wizard

First-boot assistant guides through:
1. MakeMKV installation and EULA acceptance
2. Transfer destination configuration
3. Library path setup
4. TMDB API key (optional)
5. Discord notifications (optional)

---

## 12. Development Architecture

### 12.1 Backend

- **FastAPI** — Async API with automatic OpenAPI docs
- **Celery** — Distributed task queue for rip/postprocess/transfer
- **SQLAlchemy** — ORM with Alembic migrations
- **PostgreSQL** — Primary data store
- **Redis** — Celery broker + result backend + caching

### 12.2 Frontend

- **Angular 17** — Standalone components, signals
- **Tailwind CSS** — Utility-first styling
- **RxJS** — Reactive state management
- **WebSocket** — Real-time updates

### 12.3 Container

- **Single container** with Supervisor process management
- **Embedded PostgreSQL/Redis** (or external via config)
- **Privileged helper** for drive access (isolated from main process)
- **NGINX** reverse proxy for frontend + API

---

## 13. Testing

- **Backend**: pytest with 880+ tests
- **Frontend**: Karma + Jasmine with 850+ specs
- **E2E**: Playwright for full pipeline testing
- **CI**: GitHub Actions with Docker image publishing

---

## 14. Documentation

| Guide | Description |
|-------|-------------|
| Installation | Detailed install steps, host setup |
| Docker | Image details, Compose, deployment |
| Quick start | Minimal steps to get running |
| Troubleshooting | Common fixes for drives/containers |
| Development | Architecture overview, design principles |
| DiscDB Contribution | Bundle workflow for contributing to TheDiscDB |
