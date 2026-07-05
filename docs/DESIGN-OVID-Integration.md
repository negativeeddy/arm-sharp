# OVID Integration — Architecture & Process

> **Status:** Phase 2 implemented (branch `feature/ovid-integration`)  
> **Last updated:** 2026-07-05

## 1. Overview

[OVID](https://github.com/The-Artificer-of-Ciphers-LLC/OVID) (Open Video Disc Identification Database) is a community-driven, open-source project to build a MusicBrainz-equivalent database for physical video discs (DVD, Blu-ray, 4K UHD). It provides:

- **Stable, deterministic disc fingerprints** — computed from structural data (IFO/BDMV files), not timestamps or file content.
- **A REST API** (`https://api.oviddb.org`) — lookup discs by fingerprint or UPC, submit new disc entries.
- **Rich disc metadata** — edition name, region code, main feature index, track layout, audio/subtitle streams, and cross-references to TMDB/IMDB.

Integrating OVID gives ARM-Sharp another way to positively identify discs when TheDiscDb lookup fails or returns no results.

---

## 2. OVID Project Structure (Reference)

The OVID codebase lives at `/workspaces/OVID` and contains:

| Component | Technology | Purpose |
|-----------|-----------|---------|
| `ovid-client/` | Python 3.9+ | Client library & CLI for fingerprinting discs |
| `api/` | FastAPI (Python) | REST API server (PostgreSQL) |
| `web/` | Next.js + React | Web frontend for browsing/submitting discs |
| `arm/` | Python | ARM integration wrapper |
| `docs/` | Markdown | Specification and documentation |

### 2.1 Fingerprint Algorithms

**DVD (OVID-DVD-1):**
- Reads `VIDEO_TS.IFO` (VMG) and `VTS_XX_0.IFO` files
- Extracts: VTS count, title count, PGC durations (seconds), chapter counts, audio/subtitle language codes
- Builds canonical string → SHA-256 → `dvd1-{first 40 hex chars}`
- Also generates `dvdread1-{32 hex}` alias via libdvdread Disc ID

**Blu-ray (OVID-BD-2):**
- Tier 1 (AACS): SHA-1 of `Unit_Key_RO.inf` → `bd1-aacs-{sha1}` or `uhd1-aacs-{sha1}`
- Tier 2 (Structure): Parses `BDMV/PLAYLIST/*.mpls` files (filtered to ≥60s duration) → canonical string → SHA-256 → `bd2-{40 hex}` or `uhd2-{40 hex}`

### 2.2 Key API Endpoints

| Method | Endpoint | Purpose | Auth |
|--------|----------|---------|------|
| `GET` | `/v1/disc/{fingerprint}` | Lookup by primary fingerprint or alias | None |
| `GET` | `/v1/disc/upc/{upc}` | Find all discs with given UPC | None |
| `POST` | `/v1/disc` | Submit complete disc with release metadata | JWT |
| `POST` | `/v1/disc/register` | Quick fingerprint-only registration | JWT |

### 2.3 Data Model (OVID)

```
Disc {
  fingerprint: str (unique, e.g. "dvd1-...")
  format: str ("DVD", "Blu-ray", etc.)
  region_code: str | None
  upc: str | None (barcode)
  edition_name: str | None
  disc_number: int (1 of N)
  total_discs: int
  status: str ("verified", "unverified", "disputed")
  release: { title, year, content_type, tmdb_id, imdb_id }
  titles: [{
    title_index: int,
    is_main_feature: bool,
    title_type: str | None,
    duration_secs: int | None,
    chapter_count: int | None,
    audio_tracks: [{ language, codec, channels }],
    subtitle_tracks: [{ language }]
  }]
}
```

---

## 3. ARM-Sharp Integration Points

### 3.1 Existing Disc Data (Already Collected)

Throughout the pipeline, ARM-Sharp already collects the following data that OVID can use:

| Data | Source | When Available |
|------|--------|---------------|
| `MountPoint` | `IdentifyService.CheckMountAsync()` | Mount stage |
| `DiscType` | `GetDiscType()` / `DetectDiscTypeFallbackAsync()` | Mount stage |
| `Label` | `blkid -s LABEL` | Mount stage |
| `DiscDbHash` | `DiscDbHashService.ComputeHashAsync()` | Identify stage |
| `CRC64` | `ComputeDvdCrc64Async()` | Identify stage (DVD only) |
| `VIDEO_TS/` IFO files | Mounted filesystem | Mount stage (DVD) |
| `BDMV/` structure | Mounted filesystem | Mount stage (BD) |
| Track metadata | MakeMKV output | Rip stage |
| UPC/barcode | Disc label / user input | Any time |

### 3.2 Where OVID Fits in the Pipeline

```
IdentifyService.IdentifyAsync()
  │
  ├── 1. Mount disc ──► CheckMountAsync()
  ├── 2. Detect DiscType ──► GetDiscType() / DetectDiscTypeFallbackAsync()
  ├── 3. QueryDiscDbAsync() — TheDiscDb hash computation + query (Phase 1)
  │       └── Exact match → HasNiceTitle = true, skip further lookups
  │
  ├── 4. ★ OVID fingerprint computation + API query (Phase 2)
  │       ├── Compute OVID fingerprint from IFO/BDMV structure (C# parser)
  │       ├── Query api.oviddb.org /v1/disc/{fingerprint}
  │       ├── On match: populate job metadata (title, year, TMDB ID)
  │       ├── Store OVID fingerprint on Job for later provider use
  │       └── HasNiceTitle = true → skip CRC64 / OMDB/TMDB fallbacks
  │
  ├── 5. CRC64 computation (DVD) + ARM API lookup (fallback)
  │       └── Only if !HasNiceTitle && !string.IsNullOrEmpty(job.TitleAuto)
  │
  ├── 6. OMDB/TMDB metadata search (fallback)
  │       └── Only if !HasNiceTitle
  │
  ├── 7. Compute disc fingerprint
  └── 8. Unmount disc

ArmRipperService.RipVisualMediaAsync()
  │
  ├── 1. MakeMKV rip (collect track info)
  ├── 2. RunEpisodeIdentificationAsync()
  │       └── EpisodeIdentificationOrchestrator
  │             ├── DiscDbProvider (existing)
  │             ├── ★ OvidProvider (existing)
  │             ├── DvdCompareProvider (existing)
  │             ├── TmdbProvider (existing)
  │             ├── TvdbProvider (existing)
  │             └── OmdbProvider (existing)
  └── 3. Transcode + Finalize
```

### 3.3 Integration Strategy — Two-Phase Approach

#### Phase 1: OVID Provider (Episode Identification Pipeline)

Add a new `OvidProvider` as an `IEpisodeIdentificationProvider` that:

1. Receives the OVID fingerprint (pre-computed during Identify stage) via `DiscContext`
2. Queries `GET /v1/disc/{fingerprint}` from the OVID API
3. Maps the response to `ProviderResult[]` with `Confidence.Definitive`
4. Returns track-level episode mapping based on OVID's known disc structure

**Example flow:**
- Disc is mounted → IFO files read → OVID fingerprint computed → stored on `Job.OvidFingerprint`
- Later, the orchestrator calls `OvidProvider.IdentifyAsync(context)`
- `context` contains the OVID fingerprint (passed via a new field)
- Provider calls `GET https://api.oviddb.org/v1/disc/{fingerprint}`
- Returns `ProviderResult[]` mapping each title to episode info

#### Phase 2: Identify-Stage OVID Lookup (Pre-Rip)

After the initial OVID provider is working, add an OVID lookup directly in `IdentifyService.IdentifyAsync()` to:

1. Compute the OVID fingerprint while the disc is mounted
2. Query the OVID API for release metadata
3. Populate job-level fields (title, year, TMDB ID) when TheDiscDb has no match
4. Store the OVID fingerprint on the Job for the provider pipeline

This gives ARM-Sharp two independent disc identification sources: TheDiscDb and OVID.

---

## 4. Proposed Architecture

### 4.1 New Project: `ArmMedia.OvidProvider`

```
src/ArmMedia.OvidProvider/
├── ArmMedia.OvidProvider.csproj
├── OvidProvider.cs                 # IEpisodeIdentificationProvider implementation
├── OvidProviderOptions.cs          # Configuration (API URL, API key, timeouts)
├── OvidApiClient.cs                # HTTP client for OVID REST API
├── Fingerprint/
│   ├── IfoModels.cs                # C# data models (VMGInfo, VTSInfo, PGCInfo, etc.)
│   ├── IfoParser.cs                # Pure-C# IFO binary parser (ported from Python ifo_parser.py)
│   ├── DvdFingerprinter.cs         # OVID-DVD-1 canonical string builder + SHA-256 hash
│   └── OvidDisc.cs                 # High-level service for computing fingerprints from mounted disc
└── Models/
    ├── OvidDiscLookupResponse.cs   # Response DTO from GET /v1/disc/{fp}
    └── OvidTrackInfo.cs            # Track-level DTO
```

### 4.2 Fingerprint Computation Strategy

Since the OVID fingerprinting algorithm is implemented in Python, there are three approaches:

**Option A — Shell out to `ovid` CLI (Recommended for Phase 1)**
- Install `ovid-client` Python package in the Docker image
- Call `ovid fingerprint /mnt/dev/sr0` via `CliProcessRunner`
- Parse stdout for the fingerprint string
- Pros: Zero porting effort, stays in sync with OVID upstream
- Cons: Python dependency in container; subprocess overhead

**Option B — Port IFO parser to C# (Recommended for Phase 2)**
- Port the pure-Python IFO parser (`ifo_parser.py`, `fingerprint.py`) to C#
- No dependencies on Python runtime
- Pros: No subprocess, no Python dependency, fast
- Cons: Porting effort; must stay in sync with OVID spec changes

**Option C — Run local OVID API instance**
- Deploy a local OVID API container alongside ARM-Sharp
- POST disc structure to the local API for fingerprint computation
- Pros: Clean REST integration
- Cons: Requires PostgreSQL; complex orchestration

### 4.3 New Fields on `Job` Model

```csharp
/// <summary>OVID disc fingerprint (e.g. "dvd1-a3f92c1b...").</summary>
public string? OvidFingerprint { get; set; }

/// <summary>Raw OVID API response JSON, cached for later provider use.</summary>
public string? OvidApiResponse { get; set; }

/// <summary>Whether this disc has been submitted to the OVID database.</summary>
public bool OvidSubmitted { get; set; }
```

### 4.4 New Field on `DiscContext`

```csharp
/// <summary>
/// Gets the OVID disc fingerprint, if it was computed during the identify stage.
/// Used by the OvidProvider for API lookups.
/// </summary>
public string? OvidFingerprint { get; init; }
```

### 4.5 Submission Service

`IOvidSubmitService` (in `ArmRipper.Core/Rip/`) provides disc submission to OVID:

```csharp
public interface IOvidSubmitService
{
    /// <summary>Submits a single disc's OVID fingerprint for registration.</summary>
    Task<OvidSubmitResult> SubmitDiscAsync(Job job, CancellationToken ct = default);

    /// <summary>Submits all discs that have not yet been submitted to OVID.</summary>
    Task<int> SubmitPendingAsync(CancellationToken ct = default);
}
```

The implementation calls `OvidApiClient.RegisterFingerprintAsync()` with the JWT token from settings. Registered as scoped in DI.

### 4.6 Provider Registration

In `ArmSharpServiceCollectionExtensions.AddArmMediaTvPipeline()`:

```csharp
.AddProvider<OvidProvider>()
```

Registered after `DiscDbProvider` and before `DvdCompareProvider` in the default order:

```json
"EpisodeIdentification": {
  "ProviderOrder": ["DiscDb", "Ovid", "DvdCompare", "Tmdb", "Tvdb", "Omdb", "PositionalFallback"]
}
```

### 4.7 OvidProvider Options

```csharp
public class OvidProviderOptions
{
    public const string SectionName = "OvidProvider";

    /// <summary>Base URL for the OVID API (default: https://api.oviddb.org).</summary>
    public string ApiUrl { get; set; } = "https://api.oviddb.org";

    /// <summary>Optional bearer token for authenticated operations (submission).</summary>
    public string? ApiToken { get; set; }

    /// <summary>HTTP request timeout in seconds.</summary>
    public int TimeoutSeconds { get; set; } = 30;
}
```

---

## 5. API Client Design

### 5.1 Lookup Endpoint

```csharp
/// <summary>Queries the OVID API for a disc by its fingerprint.</summary>
async Task<OvidDiscLookupResponse?> LookupByFingerprintAsync(
    string fingerprint,
    CancellationToken ct = default);
```

### 5.2 Response Mapping

Given an OVID response, the provider maps titles to provider results:

| OVID Field | ProviderResult Mapping |
|-----------|----------------------|
| `release.title` | Used for context; episodes resolved via TMDB cross-ref |
| `titles[].is_main_feature` | Identifies primary content |
| `titles[].duration_secs` | Track runtime matching |
| `titles[].title_type` | Content type classification |
| `titles[].audio_tracks` | Audio stream verification |
| `titles[].subtitle_tracks` | Subtitle stream verification |

Since OVID identifies the exact disc pressing, the provider can:
1. Return `Confidence.Definitive` for known tracks
2. Use OVID's title ordering + TMDB cross-reference to map episodes
3. Identify extras/specials via `title_type`

---

## 6. Configuration

### 6.1 New Section in `appsettings.json`

```json
"OvidProvider": {
  "ApiUrl": "https://api.oviddb.org",
  "ApiToken": "",
  "TimeoutSeconds": 30
}
```

### 6.2 New Settings in `ArmSettings`

- `OvidEnabled` (bool, default `true`) — Enable/disable OVID integration
- `OvidApiToken` (string, optional) — Bearer token for submissions
- `OvidSubmitEnabled` (bool, default `true`) — Enable/disable OVID submission feature (sends fingerprints to api.oviddb.org)

---

## 7. Implementation Plan

### Phase 1: Foundation ✅ (This Branch)

1. ✅ **Create `ArmMedia.OvidProvider` project**
   - `.csproj` referencing `ArmMedia.Core`
   - `OvidProviderOptions` class
   - `OvidApiClient` — HTTP client for OVID API lookups

2. ✅ **Create `OvidProvider`**
   - Implement `IEpisodeIdentificationProvider`
   - Query OVID API by fingerprint from `DiscContext`
   - Map results to `ProviderResult[]` array
   - Return `Confidence.Definitive` for verified OVID matches

3. ✅ **Port IFO parser to C#**
   - `IfoParser` — pure-C# binary parser for VIDEO_TS.IFO and VTS_XX_0.IFO
   - `DvdFingerprinter` — OVID-DVD-1 canonical string builder + SHA-256 hasher
   - `OvidDisc` — high-level service for computing fingerprints from mounted disc
   - Fingerprint identical to Python output (same deterministic algorithm)

4. ✅ **Add OVID fingerprint to IdentifyService**
   - Compute OVID fingerprint from mounted disc using C# parser (no Python dependency)
   - Store on `Job.OvidFingerprint`
   - Flow through to `DiscContext.OvidFingerprint`

5. ✅ **Register provider in DI**
   - Add to `ArmSharpServiceCollectionExtensions`
   - Register `OvidApiClient` via `AddHttpClient`
   - Bind `OvidProviderOptions` from configuration
   - Update default provider order to `["DiscDb", "Ovid", ...]`

6. ✅ **Database migration** — EF migration `AddOvidFields` adds `OvidFingerprint`, `OvidApiResponse`, `OvidSubmitted` columns

7. ✅ **Add project to solution** — `ArmRipper.slnx`

### Phase 2: Enhanced Identification ✅ (This Branch)

1. ✅ **OVID identify-stage integration**
   - `QueryOvidApiAsync()` called after fingerprint computation in `IdentifyAsync()`
   - Populates job title/year metadata when DiscDb has no match
   - Sets `HasNiceTitle = true` on match, preventing fuzzy fallbacks from overwriting

2. ✅ **C# fingerprint port**
   - IFO parser (`IfoParser.cs`) and fingerprinter (`DvdFingerprinter.cs`) ported to C#
   - OVID-DVD-1 fingerprint identical to Python output (same deterministic SHA-256 algorithm)
   - No Python `ovid` CLI dependency needed

3. ✅ **OVID submission feature**
   - `IOvidSubmitService` + `OvidSubmitService` — submits fingerprints to OVID via `POST /v1/disc/register`
   - `POST submit-ovid/{id}` and `POST submit-ovid/pending` API endpoints
   - Settings UI card for sending pending fingerprints
   - `OvidSubmitEnabled` setting (default `true`)

4. ⬜ **UPC lookup support** (Future)
   - Query `/v1/disc/upc/{upc}` for barcode-based identification

5. ⬜ **Local OVID API mirror** (Optional, Future)
   - Support self-hosted OVID API for air-gapped environments

---

## 8. Resolved Questions

1. ✅ **Python dependency** — Ported IFO parser and fingerprint algorithm to C#. No Python dependency needed.
2. ✅ **API rate limits** — 100 req/min unauthenticated, 500 req/min authenticated (confirmed via `api.oviddb.org` docs).
3. ✅ **OVID submission** — Implemented as opt-in feature (`OvidSubmitEnabled`), with Settings UI and API endpoints.
4. ✅ **Confidence level** — OVID match is `Confidence.Definitive` (same as DiscDb) since OVID identifies the exact disc pressing.
5. ⬜ **Self-hosted OVID** — Not yet implemented. Future consideration.

---

## 9. Related Documents

- `DESIGN-DiscDb-Integration.md` — Existing TheDiscDb integration architecture
- `DESIGN-TV-Series-Strategy.md` — Episode identification orchestrator design
- `ARCHITECTURE.md` — Overall ARM-Sharp architecture
- `/workspaces/OVID/README.md` — OVID project documentation
- `/workspaces/OVID/CONTEXT.md` — OVID terminology and concepts
