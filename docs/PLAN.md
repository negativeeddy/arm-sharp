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

## Phase 8: Complete ✅

All 4 MusicBrainz issues resolved:

| # | Issue | Fix |
|---|-------|-----|
| 8.1 | `new HttpClient()` | Typed `HttpClient` via DI (`AddHttpClient` in WebUi, manual registration in CLI) |
| 8.2 | Fire-and-forget `GetCdArtAsync` | Made `CheckMusicBrainzData`/`ProcessDiscRelease` async; `await GetCdArtAsync` |
| 8.3 | XML parsing fragility | `XDocument.Parse` wrapped in try/catch; `int.TryParse` for all numeric fields; `TryGetProperty("images")` for Cover Art API |
| 8.4 | No unit tests | 15 tests covering disc/cdstub, malformed XML, HTTP errors, cover art, track persistence |

---

---

## Phase 9: Production Docker Container ✅ (Complete)

Docker image built, verified, and ready for drop-in testing.

### 9.1 — `docker-entrypoint.sh` ✅
- `ARM_UID`/`ARM_GID` → `usermod` to match host permissions
- Default mode: web UI (foreground) + `supervise.sh` (background)
- Subcommands: `cli`, `webui`, `supervise`

### 9.2 — `watch-discs.sh` ✅
- Dynamically scans `/dev/sr*` devices
- File-based state per device (no python3 dependency)
- Lock file prevents concurrent rips
- Env config: `ARM_POLL_SECONDS`, `ARM_CLI_PATH`, `ARM_LOG_FILE`

### 9.3 — Dockerfile ✅
- Removed HandBrake build stages (base image `arm-dependencies:1.7.3`
  already has HandBrakeCLI 1.11.1 with NVENC compiled in)
- Scripts at `/opt/arm/scripts/` (avoids user's `/home/arm/scripts/` mount)
- `EXPOSE 8080`, `ASPNETCORE_URLS=http://+:8080`

### 9.4 — Image built and verified ✅
- `arm-sharp:latest` built on Tarantino
- CLI, WebUI, HandBrakeCLI all functional
- Ready for compose drop-in testing

---

## Next Session: Production Testing

Test `arm-sharp` as drop-in replacement:

1. Stop original ARM container
2. Point compose `image: arm-sharp` (or `build: .`)
3. Set `command: supervise` in compose (since original ARM uses runit/udev, we just need disc polling)
4. Test with DVD/BD inserted
5. Monitor logs + web UI

### Key compose config from existing arm service:
```yaml
environment:
  ARM_UID: "1001"
  ARM_GID: "1001"
  TZ: "America/Chicago"
devices:
  - "/dev/sr0:/dev/sr0"
  - "/dev/sg3:/dev/sg3"
  - "/dev/sr1:/dev/sr1"
  - "/dev/sg4:/dev/sg4"
volumes:
  - /mnt/data/docker/arm/home:/home/arm
  - /mnt/data/docker/arm/logs:/home/arm/logs   # watch-discs.log goes here
  - /mnt/data/docker/arm/media:/home/arm/media
  - /mnt/data/docker/arm/config:/etc/arm/config
  - /mnt/data/media:/home/arm/publish
privileged: true
gpus: all
```

### Potential issues to watch for:
- `watch-discs.sh` needs Blinuxt/udev access inside `--privileged` container — should work
- Web UI first-run sets up admin user — test login flow
- MakeMKV key handling — `EnsureKeyAsync` runs automatically
- Volume mount permissions — `ARM_UID`/`ARM_GID` entrypoint handles this
- No `/dev/sg*` needed for our app (unlike original ARM which uses `discid`)
