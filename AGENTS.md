# ARM .NET Port — Project Status

## Goal
Port the Python [automatic-ripping-machine](https://github.com/automatic-ripping-machine/automatic-ripping-machine) to C# .NET 10. The original ARM code is at `/workspaces/automatic-ripping-machine` as a reference.

## Completed
- **All core services ported:**
  - `IdentifyService` — udev-based disc identification
  - `HandBrakeService` — HandBrake CLI wrapper
  - `FfmpegService` — FFmpeg CLI wrapper
  - `MusicBrainzService` — MusicBrainz disc ID lookup
  - `OmdbService` — OMDb API metadata
  - `TmdbService` — TMDB API metadata
  - `NotificationService` — in-app notifications via SignalR
  - `ArmRipperService` — rip orchestration logic
  - `MakeMkvService` — MakeMKV CLI wrapper
  - `Conductor` — main job orchestrator
- **Data layer:** `ArmDbContext` (EF Core + SQLite), models (`Job`, `ConfigSnapshot`, `Track`, `Notification`)
- **Infrastructure:** `CliProcessRunner`, `JobLogger`
- **CLI:** `Program.cs` with full DI wiring, `--device` arg
- **Web UI:** 7 controllers + 7 Razor views + layout, Dockerfile
- **Tests:** 39 unit tests (all passing), Moq-based mocks, test helpers
- **Docker:** Production `Dockerfile` (multi-stage, builds both CLI and WebUI)

## Architecture
- `ArmRipper.Core` — class library with all business logic
- `ArmRipper.Cli` — console app entry point
- `ArmRipper.WebUi` — ASP.NET Core Razor Pages + MVC controllers
- `ArmRipper.Core.Tests` — xUnit test project

Key interfaces: `IIdentifyService`, `IArmRipperService`, `IHandBrakeService`, `IFfmpegService`, `IMusicBrainzService`

## Build
```bash
dotnet build      # solution builds 0 warnings, 0 errors
dotnet test       # 39/39 passing
```

## Running
```bash
# CLI
dotnet run --project src/ArmRipper.Cli -- --device /dev/sr0

# Web UI
dotnet run --project src/ArmRipper.WebUi
```

## Docker
```bash
docker build -t arm-dotnet .
docker run arm-dotnet webui    # runs on port 8080
docker run arm-dotnet cli --device /dev/sr0
```

## Next Steps
1. Push arm-dotnet to GitHub
2. Open in new devcontainer (this config auto-installs deps)
3. Build production Docker images, push to Docker Hub
4. Test on Tarantino (the ripping machine)
5. Iterate on any missing features / bugs discovered during real use
