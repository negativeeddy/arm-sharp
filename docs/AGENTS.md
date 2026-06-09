# ARM .NET Port — Project Status

## Goal
Port the Python [automatic-ripping-machine](https://github.com/automatic-ripping-machine/automatic-ripping-machine) to C# .NET 10 as a drop-in Docker replacement with feature parity. The original ARM code is at `/workspaces/automatic-ripping-machine` as a reference.

## Architecture
- `ArmRipper.Core` — class library with all business logic
- `ArmRipper.Cli` — console app entry point
- `ArmRipper.WebUi` — ASP.NET Core Razor Pages + MVC controllers
- `ArmRipper.Core.Tests` — xUnit test project (52 tests, all passing)
- `ArmRipper.WebUi.Tests` — xUnit integration tests (46 tests, all passing)

Key interfaces: `IIdentifyService`, `IArmRipperService`, `IHandBrakeService`, `IFfmpegService`, `IMusicBrainzService`

## Completed

### Core Services — All Ported and Reviewed
All services reviewed for bugs; several critical and medium issues fixed:

| Service | Status | Notes |
|---------|--------|-------|
| `Conductor` | ✅ Fixed | Error propagation (failure stuck in Active), log file null, HTTP client disposal |
| `IdentifyService` | ✅ Fixed | DVD detection (Directory.Exists), Blu-ray XML namespace, SearchOption, eject command, poster unmount |
| `ArmRipperService` | ✅ Fixed | Job.Stage null, DeleteRawFiles null filter, MoveFilesPostAsync series break, silent catch removed |
| `HandBrakeService` | ✅ Fixed | DurationPattern regex (no capture group → crash), dead code, track persistence in MKV path |
| `FfmpegService` | ✅ Fixed | Track Ripped not set in MKV path, raw Process (no timeout), dead ffprobe duration variable |
| `MakeMkvService` | ✅ Clean | No bugs found, but TInfo track metadata not persisted (deferred improvement) |
| `MusicBrainzService` | 🔶 Deferred to last | See `docs/FixMusicBrainz.md` — 6 issue categories, all deferred |
| `NotificationService` | ✅ Fixed | DiscType.Unknown returns friendly message instead of throwing |
| `OmdbService` / `TmdbService` | ✅ Clean | Use AddHttpClient via DI, standard pattern |

### Data Layer
- `ArmDbContext` (EF Core + SQLite) with models: `Job`, `ConfigSnapshot`, `Track`, `Notification`
- `ConfigSnapshot` tracks per-job overrides; `ArmSettings` has global defaults
- Disc metadata caching: `disc_metadata`, `disc_tracks`, `disc_track_streams` tables

### Infrastructure
- `CliProcessRunner` — wraps Process with timeout support, used by all CLI wrappers
- `JobLogger` — file-based logging per job

### Web UI
- **9 controllers** + **19 Razor views** + shared layout, SignalR hub, multi-stage Docker
- Routes: Home, Jobs (detail/search/history), Logs, Database, Settings, Notifications, Auth, API
- Cookie auth with `PasswordHasher<User>` (PBKDF2), `[Authorize]` on all controllers
- Bootstrap 4, jQuery, tablesorter, dark mode toggle

### Authentication ✅
- Login/logout with password hashing, anti-forgery tokens
- `DisableLogin` option for internal-network setups
- 6 integration tests

### Testing
- **99 Core tests** — unit tests for services, CRC64, BackgroundRipService, MakeMkvService
- **59 WebUi tests** — integration tests covering all 9 controllers and API
- **158 total, all passing**

## Build
```bash
dotnet build      # 0 warnings, 0 errors across 5 projects
dotnet test       # 158/158 passing
```

## Running
```bash
# CLI
dotnet run --project src/ArmRipper.Cli -- --device /dev/sr0

# Web UI
dotnet run --project src/ArmRipper.WebUi
```

## Hardware Testing (Tarantino)
Current environment (`/workspaces/arm-sharp`):
- **Drives:** `/dev/sr0` (BD-RE BU40N), `/dev/sr1` (DRW-24B1ST) — both accessible
- **CLI tools:** HandBrakeCLI (nvdec/nvenc enabled), makemkvcon, ffmpeg, ffprobe, abcde, eject — all installed
- **Paths:** `/opt/arm/{raw,transcode,completed,logs}` — exist, empty
- **Config:** `/etc/arm/config/` — empty (Tarantino volume mounts not set up)
- **Hardware testing complete** — DVD, Blu-ray, Audio CD, Data disc, Web UI, error recovery all tested

## Phase 7 — Complete ✅

All 9 gaps resolved:
- **7.1:** 44 MakeMkvService tests (ParseLine, GetTrackInfo*, RipTrackAsync)
- **7.2:** 13 new WebUi integration tests (Jobs, Logs, Notifications, API)
- **7.3:** BackgroundRipService singleton + IConductor — no scope leak
- **7.4:** IFfmpegService returns Task&lt;CliResult&gt; (aligns with IHandBrakeService)
- **7.5:** track.Status/track.Error set on success/failure in HandBrakeService
- **7.6:** RipperSettings entity persists ArmSettings JSON blob to DB
- **7.7:** Path.GetFileName() replaces weak Contains("/") checks
- **7.8:** Empty Views/Seed/ directory deleted
- **7.9:** Runtime already uses arm-dependencies:1.7.3 (no change needed)

## Phase 8 — MusicBrainz Fixes (Last)
- All MusicBrainzService work deferred. See `docs/FixMusicBrainz.md`
  - `new HttpClient()` → `IHttpClientFactory`
  - Fire-and-forget `GetCdArtAsync`
  - XML parsing fragility (unguarded int.Parse, no validation)
  - No unit tests for entire 308-line service
