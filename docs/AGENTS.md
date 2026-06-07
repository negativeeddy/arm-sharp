# ARM .NET Port — Project Status

## Goal
Port the Python [automatic-ripping-machine](https://github.com/automatic-ripping-machine/automatic-ripping-machine) to C# .NET 10 as a drop-in Docker replacement with feature parity. The original ARM code is at `/workspaces/automatic-ripping-machine` as a reference.

## Architecture
- `ArmRipper.Core` — class library with all business logic
- `ArmRipper.Cli` — console app entry point
- `ArmRipper.WebUi` — ASP.NET Core Razor Pages + MVC controllers
- `ArmRipper.Core.Tests` — xUnit test project (51 tests, all passing)

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
| `MusicBrainzService` | 🔶 Known gaps | `new HttpClient()` (not IHttpClientFactory), fire-and-forget GetCdArtAsync, no XML tests — deferred |
| `NotificationService` | ✅ Fixed | DiscType.Unknown returns friendly message instead of throwing |
| `OmdbService` / `TmdbService` | ✅ Clean | Use AddHttpClient via DI, standard pattern |

### Data Layer
- `ArmDbContext` (EF Core + SQLite) with models: `Job`, `ConfigSnapshot`, `Track`, `Notification`
- `ConfigSnapshot` tracks per-job overrides; `ArmSettings` has global defaults

### Infrastructure
- `CliProcessRunner` — wraps Process with timeout support, used by all CLI wrappers
- `JobLogger` — file-based logging per job

### Web UI
- 7 controllers + 7 Razor views + shared layout, SignalR hub for notifications, Dockerfile with multi-stage build
- Routes: Home, Jobs (detail/search/history), Logs, Database, Settings, Notifications

## Build
```bash
dotnet build      # 0 warnings, 0 errors across 4 projects
dotnet test       # 51/51 passing
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
- **CLI tools:** HandBrakeCLI (custom rebuild with nvdec in devcontainer; production Dockerfile still uses base image build without nvdec), makemkvcon, ffmpeg, ffprobe, abcde, eject — all installed
- **Paths:** `/opt/arm/{raw,transcode,completed,logs}` — exist, empty
- **Config:** `/etc/arm/config/` — empty (Tarantino volume mounts not set up)
- **No discs currently loaded** — need to insert media for testing

## Known Gaps (Deferred)
- `MusicBrainzService` — HttpClient creation, fire-and-forget, no XML tests, XML parsing fragility
- MakeMKV TInfo → Track persistence (track metadata lost; post-transcode workaround in place)
- Inconsistent error handling between HandBrake (best-effort) and FFmpeg (fail-fast)
- No integration tests for controllers/views
- No SignalR hub tests
- No authentication (intentional, Phase 5)
