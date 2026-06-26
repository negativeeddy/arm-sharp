# ARM .NET Port — Project Status

## Goal
Port the Python [automatic-ripping-machine](https://github.com/automatic-ripping-machine/automatic-ripping-machine) to C# .NET 10 as a drop-in Docker replacement with feature parity. The original ARM code is at `/workspaces/automatic-ripping-machine` as a reference.

## Architecture
- `ArmRipper.Core` — class library with all business logic
- `ArmRipper.Cli` — console app entry point
- `ArmRipper.WebUi` — ASP.NET Core Razor Pages + MVC controllers
- `ArmRipper.Core.Tests` — xUnit test project
- `ArmRipper.WebUi.Tests` — xUnit integration tests

Key interfaces: `IIdentifyService`, `IArmRipperService`, `IHandBrakeService`, `IFfmpegService`, `IMusicBrainzService`

### Data Layer
- `ArmDbContext` (EF Core + SQLite) with models: `Job`, `ConfigSnapshot`, `Track`, `Notification`
- `ConfigSnapshot` tracks per-job overrides; `ArmSettings` has global defaults
- Disc metadata caching: `disc_metadata`, `disc_tracks`, `disc_track_streams` tables

### Infrastructure
- `CliProcessRunner` — wraps Process with timeout support, used by all CLI wrappers
- `JobLogger` — file-based logging per job

### Web UI
- 9 controllers + 19 Razor views + shared layout, SignalR hub, multi-stage Docker
- Routes: Home, Jobs (detail/search/history), Logs, Database, Settings, Notifications, Auth, API
- Cookie auth with `PasswordHasher<User>` (PBKDF2), `[Authorize]` on all controllers
- Bootstrap 4, jQuery, tablesorter, dark mode toggle

### Authentication
- Login/logout with password hashing, anti-forgery tokens
- `DisableLogin` option for internal-network setups

### Testing
- Core unit tests + WebUi integration tests, all passing

## Build
```bash
dotnet build      # 0 warnings, 0 errors across 5 projects
dotnet test       # all passing
```

## Running
```bash
# CLI
dotnet run --project src/ArmRipper.Cli -- --device /dev/sr0

# Web UI
dotnet run --project src/ArmRipper.WebUi

# Docker
docker run --privileged --rm -p 8080:8080 \
  -v /mnt/data/docker/arm/home:/home/arm \
  -v /mnt/data/docker/arm/logs:/home/arm/logs \
  -v /mnt/data/docker/arm/media:/home/arm/media \
  -v /mnt/data/docker/arm/config:/etc/arm/config \
  -v /mnt/data/media:/home/arm/publish \
  --device /dev/sr0 --device /dev/sr1 \
  arm-sharp
```
