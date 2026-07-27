# Research: MKV Auto Release vs ARM Sharp

**Date:** 2026-07-26  
**Status:** Complete  
**Scope:** Feature comparison, TV series identification analysis, recommendations for ARM Sharp

---

## Table of Contents

1. [Index](#index) — Overview and key findings
2. [MKV Auto Feature Set](./mkv-auto-features.md) — Major features and architecture
3. [TV Series & Box Set Identification](./tv-series-identification.md) — Deep dive into TV handling
4. [Comparison Matrix](./comparison-matrix.md) — Feature-by-feature comparison
5. [Recommendations](./recommendations.md) — Actionable changes for ARM Sharp

---

## Quick Summary

MKV Auto is a Python/FastAPI/Angular application with a sophisticated multi-stage pipeline for disc ripping. Its TV series handling is significantly more advanced than ARM Sharp's current implementation, featuring:

- **TMDB API integration** with title normalization, episode catalog lookups, and search-by-title
- **TheDiscDB integration** with content-hash-based disc identification and metadata enrichment
- **Box set management** with first-class boxset selector, release linking, and multi-disc grouping
- **Episode identification** via position-based mapping from TMDB season episode catalogs
- **Path templates** for configurable output naming (Plex/Jellyfin compatible)
- **Duplicate group detection** for obfuscated Blu-ray playlists
- **Auto-rip on insert** with intelligent DiscDB hit/miss handling

ARM Sharp has the foundation for most of these features but lacks the polish, UI sophistication, and some backend logic that MKV Auto has developed.

---

## Key Files Referenced

### MKV Auto
- `Backend/core/tmdb_client.py` — TMDB v3 API client with normalization, search, episode catalogs
- `Backend/core/tmdb_scraper.py` — TMDB HTML page scraping
- `Backend/core/discdb_enrichment.py` — DiscDB metadata overlay onto scan results
- `Backend/core/discdb_finalize.py` — DiscDB contribution export
- `Backend/core/discdb_import.py` — DiscDB content hash computation
- `Backend/core/title_type_normalize.py` — Canonical title type strings
- `Backend/core/path_templates.py` — Configurable output path templates
- `Backend/core/segment_reorder.py` — Obfuscated playlist detection and resolution
- `Backend/core/rip_selection.py` — Selective rip set construction
- `Backend/core/auto_rip.py` — Auto-rip on insert logic
- `Backend/core/bd_mpls.py` — Blu-ray MPLS playlist parser
- `Backend/parsing/disc_parser.py` — Disc payload normalization
- `Backend/api/routers/discdb.py` — DiscDB API routes
- `Backend/api/routers/disc_previews.py` — Preview serving routes
- `Frontend/src/app/components/boxset-selector/` — Boxset UI component
- `Frontend/src/app/components/movie-selector/` — Movie/TV selector with TMDB search
- `Frontend/src/app/components/film-label/` — Film metadata entry component
- `Frontend/src/app/components/title-label/` — Title labeling with duplicate groups

### ARM Sharp
- `src/ArmRipper.Core/Rip/Conductor.cs` — Job orchestration
- `src/ArmRipper.Core/Rip/IdentifyService.cs` — Disc identification pipeline
- `src/ArmRipper.Core/Rip/TrackMapperService.cs` — DiscDb title-to-track matching
- `src/ArmRipper.Core/Rip/DiscDbLookupAdapter.cs` — DiscDb provider adapter
- `src/ArmRipper.Core/Rip/DiscDbMappingService.cs` — DiscDb cache management
- `src/ArmRipper.Core/Rip/DiscDbQueryService.cs` — DiscDb API queries
- `src/ArmRipper.Core/Metadata/TmdbService.cs` — TMDB search (basic)
- `src/ArmRipper.Core/Models/Job.cs` — Job model (has SeriesTmdbId, SeasonNumber)
- `src/ArmRipper.Core/Models/Track.cs` — Track model (has EpisodeNumber, EpisodeTitle, ContentType)
- `src/ArmMedia.TmdbProvider/TmdbProvider.cs` — TMDB episode identification provider
- `docs/DESIGN-TV-Series-Strategy.md` — Existing TV series architecture design
- `docs/PLAN.md` — Development plan
