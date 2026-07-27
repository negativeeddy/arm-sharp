# Feature Comparison Matrix

**MKV Auto Release vs ARM Sharp**  
**Last updated:** 2026-07-26

---

## 1. Core Ripping

| Feature | MKV Auto | ARM Sharp | Winner |
|---------|----------|-----------|--------|
| MakeMKV integration | ✅ Direct CLI | ✅ CLI adapter | Tie |
| DVD ripping | ✅ | ✅ | Tie |
| Blu-ray ripping | ✅ | ✅ | Tie |
| 4K UHD ripping | ✅ | ✅ | Tie |
| HLS streaming | ✅ | ❌ | MKV Auto |
| Auto-rip on insert | ✅ | ❌ | MKV Auto |
| Multi-drive concurrent | ✅ | ✅ | Tie |
| Drive-swap detection | ✅ | ❌ | MKV Auto |
| Selective rip set | ✅ | ❌ | MKV Auto |
| MKV merging | ✅ | ❌ (not needed) | MKV Auto |

---

## 2. Disc Identification

| Feature | MKV Auto | ARM Sharp | Winner |
|---------|----------|-----------|--------|
| TheDiscDB content hash | ✅ GraphQL | ✅ REST | Tie |
| DiscDB track mapping | ✅ Enrichment overlay | ✅ TrackMapperService | ARM Sharp (weighted matching) |
| OVID fingerprint | ❌ | ✅ | ARM Sharp |
| DiscDB contribution | ✅ Full export | ❌ | MKV Auto |
| DiscDB cache | ✅ Redis | ✅ Redis | Tie |
| Content hash computation | ✅ | ✅ | Tie |

---

## 3. Metadata Services

| Feature | MKV Auto | ARM Sharp | Winner |
|---------|----------|-----------|--------|
| TMDB API client | ✅ Full v3 client | ⚠️ Basic search | MKV Auto |
| TMDB title normalization | ✅ Season/disc/edition extraction | ❌ | MKV Auto |
| TMDB season/episode catalogs | ✅ | ❌ | MKV Auto |
| TMDB search scoring | ✅ Title overlap + popularity + year | ❌ | MKV Auto |
| TMDB auto-suggestion | ✅ On scan completion | ❌ | MKV Auto |
| OMDB provider | ❌ | ✅ | ARM Sharp |
| TVDB provider | ❌ | ✅ | ARM Sharp |
| FileBot CLI | ❌ | ✅ | ARM Sharp |
| DiscDb provider | ✅ | ✅ | Tie |
| OVID provider | ❌ | ✅ | ARM Sharp |
| Provider pipeline | ❌ Monolithic | ✅ Orchestrator design | ARM Sharp |

---

## 4. TV Series Handling

| Feature | MKV Auto | ARM Sharp | Winner |
|---------|----------|-----------|--------|
| Series identification | ✅ Multiple paths | ⚠️ DiscDb + basic fallback | MKV Auto |
| Season selection UI | ✅ | ❌ | MKV Auto |
| Episode assignment | ✅ TMDB catalog + position | ⚠️ DiscDb only + manual | MKV Auto |
| Auto-fill from position | ✅ | ❌ | MKV Auto |
| Duplicate groups | ✅ Segment map grouping | ❌ | MKV Auto |
| Title type classification | ✅ Canonical types | ⚠️ Basic types | MKV Auto |
| Multi-part episode handling | ✅ | ❌ | MKV Auto |
| Series path templates | ✅ Configurable | ⚠️ Hardcoded | MKV Auto |

---

## 5. Collection Management

| Feature | MKV Auto | ARM Sharp | Winner |
|---------|----------|-----------|--------|
| Movie model | ✅ TMDB-backed | ❌ | MKV Auto |
| Release model | ✅ Edition tracking | ❌ | MKV Auto |
| Boxset model | ✅ Full CRUD + UI | ❌ | MKV Auto |
| Boxset selector UI | ✅ Search/create/edit | ❌ | MKV Auto |
| Movie selector UI | ✅ TMDB search + URL | ❌ | MKV Auto |
| Release linking | ✅ | ❌ | MKV Auto |
| Library page | ✅ Compact title cards | ❌ | MKV Auto |
| "Already in Library" detection | ✅ | ❌ | MKV Auto |

---

## 6. Output & Transfer

| Feature | MKV Auto | ARM Sharp | Winner |
|---------|----------|-----------|--------|
| Path templates | ✅ Configurable | ⚠️ Hardcoded | MKV Auto |
| Plex output structure | ✅ | ❌ | MKV Auto |
| Jellyfin output structure | ✅ | ❌ | MKV Auto |
| Local transfer | ✅ | ❌ | MKV Auto |
| SMB transfer | ✅ | ❌ | MKV Auto |
| rsync transfer | ✅ | ❌ | MKV Auto |
| NFS transfer | ✅ | ❌ | MKV Auto |
| Conflict resolution | ✅ | ❌ | MKV Auto |
| Custom templates | ✅ | ❌ | MKV Auto |

---

## 7. UI/UX

| Feature | MKV Auto | ARM Sharp | Winner |
|---------|----------|-----------|--------|
| Frontend framework | Angular 17 | Razor Pages | Different stacks |
| Real-time updates | WebSocket | SignalR | Tie (different patterns) |
| Mobile responsive | ✅ | ⚠️ Limited | MKV Auto |
| Title preview clips | ✅ | ❌ | MKV Auto |
| Duplicate group expand/collapse | ✅ | ❌ | MKV Auto |
| Chip-based metadata display | ✅ | ❌ | MKV Auto |
| Obfuscation badge | ✅ | ❌ | MKV Auto |
| Setup wizard | ✅ | ❌ | MKV Auto |
| Settings page | ✅ Comprehensive | ❌ | MKV Auto |
| Dark/light theme | ✅ | ❌ | MKV Auto |
| Toast notifications | ✅ WebSocket | ✅ SignalR | Tie |
| Discord notifications | ✅ | ❌ | MKV Auto |

---

## 8. Architecture & DevOps

| Feature | MKV Auto | ARM Sharp | Winner |
|---------|----------|-----------|--------|
| Language | Python 3 | C# .NET 10 | Preference |
| Backend framework | FastAPI | ASP.NET Core | Preference |
| Database | PostgreSQL | SQLite | ARM Sharp (simpler) |
| Cache | Redis | In-memory / Redis | Tie |
| Task queue | Celery | Background services | Preference |
| Container | Single Docker | Docker Compose | ARM Sharp (multi-service) |
| ORM | SQLAlchemy | EF Core | Preference |
| Migration tool | Alembic | EF Migrations | Preference |
| Process manager | Supervisor | Kestrel / Docker | Preference |
| Test coverage | 880+ backend, 850+ frontend | Growing | MKV Auto (more mature) |
| CI/CD | GitHub Actions | GitHub Actions | Tie |
| Documentation | Extensive guides | Growing docs | MKV Auto (more mature) |

---

## 9. Summary

### MKV Auto Strengths
- **TV series workflow**: TMDB integration, season/episode catalogs, auto-fill
- **Collection management**: Boxsets, releases, library page
- **Transfer system**: Multiple modes, path templates, Plex/Jellyfin awareness
- **Obfuscation handling**: Duplicate groups, segment reorder, selective rip
- **UI/UX**: Mobile responsive, preview clips, setup wizard, settings
- **Documentation**: Comprehensive guides and troubleshooting

### ARM Sharp Strengths
- **Provider pipeline**: Extensible architecture (EpisodeIdentificationOrchestrator)
- **OVID fingerprinting**: Alternative disc identification method
- **Richer metadata providers**: TMDB, OMDB, TVDB, FileBot, DiscDb, OVID
- **.NET ecosystem**: Strong typing, dependency injection, testability
- **SQLite**: Simpler deployment, no external DB dependency
- **SignalR**: More integrated real-time update pattern
- **Weighted track matching**: Better handling of edge cases in DiscDb mapping

### Overall Assessment

**MKV Auto is more feature-complete** for the end-to-end ripping workflow, particularly around:
- TV series identification and episode assignment
- Collection management (boxsets, releases, library)
- Output configuration and transfer

**ARM Sharp has a better architecture** for extensibility and maintenance:
- Provider pipeline pattern
- Strong typing and DI
- Multiple metadata sources
- More testable design

**Recommendation:** ARM Sharp should adopt MKV Auto's TV workflow features while maintaining its architectural advantages. The key gaps are TMDB season/episode catalogs, boxset management, and output configuration.
