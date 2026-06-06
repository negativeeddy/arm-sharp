# ARM Sharp Architecture

C# .NET 10 port of [automatic-ripping-machine](https://github.com/automatic-ripping-machine/automatic-ripping-machine) — a drop-in Docker replacement for optical disc ripping.

## Project Structure

```
ArmRipper.slnx
├── src/
│   ├── ArmRipper.Core/          # Business logic (class library)
│   │   ├── Configuration/       # ArmSettings, ArmYamlConfigLoader
│   │   ├── Infrastructure/      # CliProcessRunner, JobLogger, ArmDbContext
│   │   ├── Metadata/            # OmdbService, TmdbService
│   │   ├── Models/              # Job, Track, ConfigSnapshot, User, etc.
│   │   ├── Notifications/       # NotificationService, INotificationBroadcaster
│   │   └── Rip/                 # Conductor, IdentifyService, ArmRipperService,
│   │                            # HandBrakeService, FfmpegService, MakeMkvService,
│   │                            # MusicBrainzService, DvdCrc64
│   ├── ArmRipper.Cli/           # Console entry point (headless operation)
│   └── ArmRipper.WebUi/         # ASP.NET Core MVC + Razor Pages web interface
│       ├── Controllers/         # 9 controllers
│       ├── Views/               # 7 view directories + shared layout
│       ├── Hubs/                # SignalR notification hub
│       └── wwwroot/             # Static assets (JS, CSS)
└── tests/
    └── ArmRipper.Core.Tests/    # xUnit tests (52+)
```

## Architecture Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Language | C# .NET 10 | Type-safe, faster than Python, excellent tooling |
| ORM | EF Core + SQLite | Native .NET, LINQ, migrations support |
| Web UI | ASP.NET Core MVC + Razor Pages | Built-in, no JS framework dependency |
| Auth | Cookie auth + PasswordHasher | Simple, no external dependencies, internal-network trust model |
| Container base | `arm-dependencies:1.7.3` | Drop-in replacement, all tools pre-installed |
| Metadata | OMDB + TMDB | Same APIs as original ARM |
| Notifications | Apprise-compatible | Retains 30+ notification services from original |
| Configuration | YAML overlay via InMemoryCollection | Reads existing `arm.yaml` with 50+ key mappings |

## Rip Pipeline

The core pipeline flows through the `Conductor` orchestrator:

```
Device connected
    │
    ▼
Mount disc (/dev/sr0 → /mnt/dev/sr0)
    │
    ▼
Identify disc type ───► Audio CD ──► MusicBrainz lookup ──► abcde rip
    │                       │
    │                       └──────────────────────────────────►
    │                       ▼
    ├──► DVD ──► CRC64 computation ──► ARM API lookup ──► MakeMKV rip
    │                                                     │
    ├──► Blu-ray ──► BD metadata parse ──► MakeMKV rip───┤
    │                                                     │
    └──► Data disc ──► dd raw rip ────────────────────────┤
                                                          │
                                                          ▼
                                              HandBrake transcode (or ffmpeg)
                                                          │
                                                          ▼
                                              File move to completed path
                                                          │
                                                          ▼
                                              Cleanup (eject, raw files)
                                                          │
                                                          ▼
                                              Notifications (Apprise, etc.)
```

### Key Interfaces

- `IIdentifyService` — Disc detection and type classification
- `IArmRipperService` — Rip orchestration (MakeMKV, file moves, cleanup)
- `IHandBrakeService` — Video transcoding via HandBrakeCLI
- `IFfmpegService` — Video transcoding via ffmpeg
- `IMusicBrainzService` — Audio CD metadata lookup
- `ICliProcessRunner` — External process execution with timeout
- `INotificationBroadcaster` — Real-time event broadcast (SignalR)

### Service Lifetimes

| Service | Lifetime | Notes |
|---------|----------|-------|
| `CliProcessRunner` | Singleton | Stateless, reusable |
| `ArmDbContext` | Scoped | EF Core convention |
| `IdentifyService` | Scoped | Per-job state |
| `ArmRipperService` | Scoped | Per-job state |
| `HandBrakeService` | Scoped | Per-job state |
| `FfmpegService` | Scoped | Per-job state |
| `MakeMkvService` | Scoped | Per-job state |
| `MusicBrainzService` | Scoped | Per-job state |
| `NotificationService` | Scoped | Per-job state |
| `OmdbService` | Transient (via HttpClientFactory) | Stateless |
| `TmdbService` | Transient (via HttpClientFactory) | Stateless |
| `Conductor` | Scoped | Orchestrator |
| `SignalRNotificationBroadcaster` | Singleton | One connection hub |

## Data Model

### Tables (SQLite via EF Core)

- **jobs** — Rip job state, disc info, metadata, timestamps
- **tracks** — Per-track status, filenames, encoding parameters
- **config** — Per-job configuration snapshot (overrides ArmSettings defaults)
- **system_drives** — Detected optical drives (udev scan)
- **system_info** — Host hardware info
- **notifications** — Event log for UI display
- **ui_settings** — User preferences (theme, refresh rate)
- **users** — Authentication (username, bcrypt hash, role)

### Configuration

`ArmSettings` provides global defaults bound from `appsettings.json` + YAML overlay.

`ConfigSnapshot` stores per-job overrides at rip time so changing global settings doesn't affect in-progress jobs.

### YAML Key Mapping

`ArmYamlConfigLoader` maps 50+ ARM `UPPER_CASE` config keys to `Arm:CamelCase` settings:

| ARM Key | Config Key | Default |
|---------|-----------|---------|
| `RAW_PATH` | `Arm:RawPath` | `/home/arm/media/raw` |
| `TRANSCODE_PATH` | `Arm:TranscodePath` | `/home/arm/media/transcode` |
| `COMPLETED_PATH` | `Arm:CompletedPath` | `/home/arm/media` |
| `LOGPATH` | `Arm:LogPath` | `/home/arm/logs` |
| `DBFILE` | `Arm:DbFile` | `/etc/arm/config/arm.db` |
| `RIPMETHOD` | `Arm:RipMethod` | `mkv` |
| `HB_PRESET_DVD` | `Arm:HbPresetDvd` | `Very Fast 1080p30` |
| `OMDB_API_KEY` | `Arm:OmdbApiKey` | — |
| `WEBSERVER_PORT` | `Arm:WebServerPort` | `8080` |

Full key list in `src/ArmRipper.Core/Configuration/ArmYamlConfigLoader.cs:7-60`.

## Web UI

- **Auth**: Cookie-based with `PasswordHasher<User>` (PBKDF2), `[Authorize]` on all controllers
- **SignalR hub**: `/hubs/notifications` for real-time updates
- **Controllers**: Home, Jobs, History, Logs, Database, Settings, Notifications, Auth, API
- **Static assets**: Bootstrap 4, jQuery, tablesorter, SignalR client — loaded via CDN with integrity hashes
- **API endpoints**: `/api/health`, `/api/jobs`, `/api/drives`, `/api/stats`, `/api/abandon/{id}`, `/api/change-params`, `/api/log`

## Docker

Multi-stage build:
1. **Build stage**: `mcr.microsoft.com/dotnet/sdk:10.0` — restore + publish
2. **Runtime stage**: `automaticrippingmachine/arm-dependencies:1.7.3` + .NET 10 runtime

Entry point (`docker-entrypoint.sh`):
- `docker run <image> cli [options]` → runs `ArmRipper.Cli`
- `docker run <image> webui` → runs `ArmRipper.WebUi` on port 8080

### Mount Conventions (matching ARM)

| Host path | Container path | Purpose |
|-----------|---------------|---------|
| `/dev/sr*` | `/dev/sr*` | Optical drives (requires `--privileged`) |
| `/opt/arm` | `/home/arm/media` | Raw, transcode, completed output |
| `/opt/arm/logs` | `/home/arm/logs` | Log files |
| `/etc/arm/config` | `/etc/arm/config` | Config files (arm.yaml, arm.db) |

### GPU Passthrough

- NVIDIA: `--gpus all` (requires `nvidia-container-toolkit` on host)
- Intel QSV: `--device /dev/dri:/dev/dri`
- AMD VAAPI: `--device /dev/dri:/dev/dri`
