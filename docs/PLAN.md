# ARM .NET Port — Development Plan

## Strategy
- **Parity-first**: Match all original ARM Web UI features, then test hardware
- **Drop-in Docker**: Container matches ARM's volume/device/port conventions
- **OSS-ready**: Decisions documented, no proprietary deps

## Architecture Decisions (for OSS documentation)

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Language | C# .NET 10 | Type-safe, faster than Python, excellent tooling |
| ORM | EF Core + SQLite | Native .NET, LINQ, migrations support |
| Web UI | ASP.NET Core MVC + Razor Pages | Built-in, no JS framework dependency |
| Container base | `arm-dependencies:1.7.3` | Drop-in replacement, all tools pre-installed |
| Auth | Cookie auth + PasswordHasher | Simple, no external dependencies, internal-network trust model |
| Metadata | OMDB + TMDB (same as ARM) | Both APIs remain available |
| Notifications | Apprise-compatible | Retains 30+ notification services from original |

---

## Phase 0: Foundation & Tarantino Devcontainer

### 0.1 — Create Tarantino devcontainer config
- New `.devcontainer/tarantino.json` (or switchable profile)
- **Base:** Use `arm-dependencies:1.7.3` + install .NET 10 SDK via Microsoft package feed
- **Run args:** `--privileged` (for `/dev/sr*` access) + `--gpus all` (NVIDIA)
- **Volume mounts:** ARM config/paths (matches Docker volumes)
- **postCreate:** dotnet restore + build + test

### 0.2 — Create `ARCHITECTURE.md` ✅
- Project structure map
- Configuration reference (arm.yaml → ArmSettings mapping)
- How the rip pipeline works (identify → rip → transcode → move → notify)

### 0.3 — Create `docs/` directory for extended docs ✅
- `docs/configuration.md` — all config knobs
- `docs/docker.md` — Docker usage, GPU passthrough, drive mapping
- `docs/development.md` — how to build, test, contribute
- `docs/FixMusicBrainz.md` — deferred MusicBrainz issues

---

## Phase 1: Fix Critical Runtime Gaps ✅ (All Complete)

### 1.1 — CRC64 computation ✅
- `DvdCrc64.Compute()` matches Python `pydvdid` 1:1
- Cross-validated: `571d8fb21eb8fe4b` from both Python and C#

### 1.2 — Register all Core services in WebUi DI ✅
- All 15+ services registered: IdentifyService, ArmRipperService, HandBrakeService, FfmpegService, MakeMkvService, MusicBrainzService, NotificationService, OmdbService, TmdbService, CliProcessRunner, Conductor, SignalR, auth, MVC

### 1.3 — Add missing ArmSettings properties ✅
- `HbArgsDvd`, `HbArgsBd`, `FfmpegCli`, `FfmpegPreFileArgs`, `FfmpegPostFileArgs`
- `ArmApiKey`, `EmbyServer`, `EmbyPort`, `EmbyApiKey`, `EmbyRefresh`
- `PbKey`, `IftttKey`, `PoUserKey`, `BashScript`, `JsonUrl`, `Apprise`
- `UiBaseUrl`, `ExtrasSub`

### 1.4 — Add SignalR hub ✅
- `/hubs/notifications` hub wired via `SignalRNotificationBroadcaster`
- `StreamLog` for real-time log streaming via `IAsyncEnumerable<string>`

### 1.5 — Add health check endpoint ✅
- `GET /api/health` → `{ "status": "healthy", "timestamp": "...", "version": "..." }`

---

## Phase 2: Web UI Feature Parity

### 2.1 — Add missing controllers + views (9 pages) ✅

| Controller | Route | Status |
|-----------|-------|--------|
| `HomeController` | `/` | ✅ |
| `JobsController` | `/jobs/*` | ✅ |
| `HistoryController` | `/history` | ✅ |
| `LogsController` | `/logs`, `/logs/view`, `/logs/download` | ✅ |
| `DatabaseController` | `/database`, `/database/update`, `/database/import`, `/database/delete` | ✅ |
| `SettingsController` | `/settings`, `/settings/scan`, `/settings/eject`, `/settings/sysinfo`, `/settings/start-rip` | ✅ |
| `NotificationsController` | `/notifications`, `/api/notifications/*` | ✅ |
| `ApiController` | `/api/health`, `/api/jobs`, `/api/drives`, `/api/stats`, `/api/abandon`, `/api/change-params`, `/api/log` | ✅ |
| `AuthController` | `/auth/login`, `/auth/logout` | ✅ |

### 2.2 — Enhance existing views 🟡 (In Progress)

| View | Enhancement | Status |
|------|-------------|--------|
| `Home/Index.cshtml` | Server stats, AJAX job refresh | 🟡 Partial |
| `Jobs/JobDetail.cshtml` | Poster display, metadata, config snapshot, edit controls, track editing | 🟡 Partial |
| `Jobs/TitleSearch.cshtml` | Result grid with posters, custom title form, change params form | 🟡 Partial |
| `Settings/Index.cshtml` | 7-tab layout (ripper, UI, abcde, Apprise, sysinfo) | ✅ Done |
| `History/Index.cshtml` | Pagination, sortable columns | ✅ Done |
| `Notifications/Index.cshtml` | Bell badge in nav | 🟡 Partial |
| `Shared/_Layout.cshtml` | Nav items: Logs, Database, notification badge | ✅ Done |

### 2.3 — Client-side assets ✅
- Bootstrap 4, jQuery, tablesorter, dark mode toggle — all served from CDN with integrity hashes

### 2.4 — Drive management ✅
- `/settings/scan` — udev scan for optical drives
- Eject button per drive
- Drive mode toggle (auto/manual)

---

## Phase 3: Production Docker + Tarantino Devcontainer

### 3.1 — Refactor Dockerfile to use ARM base image
- **Target:** `FROM automaticrippingmachine/arm-dependencies:1.7.3 AS base` then add .NET runtime
- Keep us as drop-in replacement — same udev rules, mount points, service structure

### 3.2 — Tarantino devcontainer profile
- `.devcontainer/tarantino/devcontainer.json`
- Base: `arm-dependencies` + .NET 10 SDK
- Run args: `--privileged`, `--gpus all`, volume mounts

### 3.3 — GPU passthrough documentation
- NVIDIA: `--gpus all`
- Intel QSV: `--device /dev/dri:/dev/dri`
- AMD VAAPI: `--device /dev/dri:/dev/dri`

### 3.4 — Mount point conventions 🟡
- Defaults exist in `ArmSettings` — document fully

---

## Phase 4: Hardware Testing on Tarantino ✅ (Complete)

### 4.1 — DVD pipeline test ✅
### 4.2 — Blu-ray pipeline test ✅
### 4.3 — Audio CD test ✅
### 4.4 — Data disc test ✅
### 4.5 — Web UI test ✅
### 4.6 — Error recovery tests ✅

---

## Phase 5: OSS Polish

### 5.1 — Authentication ✅
- Cookie auth with `PasswordHasher<User>` (PBKDF2)
- `[Authorize]` on all controllers except login/error/setup
- `DisableLogin` option for internal-network setups
- 6 integration tests covering login flows

### 5.2 — CI/CD pipeline
- GitHub Actions: `dotnet build` + `dotnet test` on PR
- Docker build + push to GHCR on tag
- Multi-arch builds (linux/amd64, linux/arm64)

### 5.3 — Docker Hub publish
- `docker buildx build --platform linux/amd64,linux/arm64 ...`
- Tags for versions

### 5.4 — README + contribution guide
- Project overview
- Quick start (Docker)
- Development setup
- Configuration reference
- FAQ / troubleshooting

---

## Phase 6: Disc Metadata Caching ✅ (Complete)

### 6.1 — Disc fingerprint ✅
- Fingerprint: `{VolumeLabel}::{SectorCount}`
- Computed in `IdentifyService.ComputeDiscFingerprintAsync`
- Stored as `Job.DiscFingerprint`

### 6.2 — Database tables (3 new) ✅
- `disc_metadata`, `disc_tracks`, `disc_track_streams`

### 6.3 — SINFO expansion ✅
- `StreamId` enum: 12 new field IDs
- `GetTrackInfoAsync` captures all stream types

### 6.4 — Cache flow ✅
- Cache lookup by fingerprint → returns stored tracks or runs `makemkvcon info`

### 6.5 — MakeMkvStreamCodes ✅
- Refactored to `MakeMkvStreamCodes.Video(6201)`, `.Audio(6202)`, `.Subtitle(6203)`

---

## Phase 7: Complete ✅

All 9 gaps resolved in order:

| # | Gap | Solution |
|---|-----|----------|
| 7.1 | MakeMkvService tests | 44 tests covering ParseLine, GetTrackInfo*, RipTrackAsync, etc. |
| 7.2 | Expand integration test coverage | 13 new WebUi tests (Jobs, Logs, Notifications, API) |
| 7.3 | StartRip fire-and-forget + scope leak | `BackgroundRipService` singleton with per-request scope + `IConductor` |
| 7.4 | Align HandBrake/FFmpeg signatures | `IFfmpegService` now returns `Task<CliResult>` (same as `IHandBrakeService`) |
| 7.5 | HandBrakeService error tracking | `track.Status`/`track.Error` set on success and failure in all 3 methods |
| 7.6 | SaveRipper persistence | `RipperSettings` entity stores `ArmSettings` as JSON blob in DB |
| 7.7 | Path traversal protection | `Path.GetFileName()` replaces weak `Contains("/")` checks |
| 7.8 | Empty Views/Seed/ cleanup | Deleted unused scaffolding directory |
| 7.9 | Dockerfile ARM base image | Runtime already uses `arm-dependencies:1.7.3` (no change needed) |

---

## Phase 8: MusicBrainz Fixes (Current)

All MusicBrainzService work is deferred until everything else is complete. See `docs/FixMusicBrainz.md` for full issue catalog.

### 8.1 — Inject IHttpClientFactory
- Replace `new HttpClient()` with `IHttpClientFactory` (lines 73, 277)

### 8.2 — Fix fire-and-forget GetCdArtAsync
- Await properly or restructure into job flow (line 170)

### 8.3 — XML parsing hardening
- Guard all `int.Parse` calls (lines 167, 195, 227)
- Use `TryGetProperty` instead of `GetProperty` (line 283)
- Handle missing namespaces gracefully

### 8.4 — Add unit tests
- Dedicated `MusicBrainzServiceTests.cs` with mocked CLI + HTTP responses
- Test all 8 untested methods

---

## Immediate Next Steps

1. **Phase 8** — MusicBrainz fixes (last remaining gap)
