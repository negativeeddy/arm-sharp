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
| Auth | Skipped (phase 5) | Original ARM trusts internal network; revisit later |
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

### 0.2 — Create `ARCHITECTURE.md`
- Port architecture decisions (table above)
- Project structure map
- Configuration reference (arm.yaml → ArmSettings mapping)
- How the rip pipeline works (identify → rip → transcode → move → notify)

### 0.3 — Create `docs/` directory for extended docs
- `docs/configuration.md` — all config knobs
- `docs/docker.md` — Docker usage, GPU passthrough, drive mapping
- `docs/development.md` — how to build, test, contribute

---

## Phase 1: Fix Critical Runtime Gaps

### 1.1 — CRC64 computation (`src/ArmRipper.Core/Rip/IdentifyService.cs:171-180`)
- **Current:** Returns `"0000000000000000"` — DVD identification via ARM API always fails
- **Target:** Port `pydvdid` CRC64 algorithm from Python
  - Need to read raw DVD sector data from device
  - Compute CRC64 matching the original algorithm
  - Reference: `/workspaces/automatic-ripping-machine` (look for pydvdid or CRC64 implementation)
- **Alternative:** Call `makemkvcon` to get disc ID as fallback
- **Test:** Unit test with known CRC64 values from test vectors

### 1.2 — Register all Core services in WebUi DI (`src/ArmRipper.WebUi/Program.cs`)
- **Current:** WebUi only registers `ArmDbContext`. No ripping services available
- **Target:** Register all services (IdentifyService, ArmRipperService, HandBrakeService, FfmpegService, MakeMkvService, MusicBrainzService, NotificationService, OmdbService, TmdbService, CliProcessRunner, Conductor)
- **Gating factor:** Some services need `IOptions<ArmSettings>` — ensure `ArmSettings` is bound in WebUi too
- **Add `appsettings.json`** to WebUi project with default config

### 1.3 — Add missing ArmSettings properties
- `HbArgsDvd`, `HbArgsBd` (defined in ConfigSnapshot but not in ArmSettings)
- `FfmpegCli`, `FfmpegPreFileArgs`, `FfmpegPostFileArgs`
- `ArmApiKey` (for ARM central API)
- `EmbyServer`, `EmbyPort`, `EmbyApiKey`, `EmbyRefresh`
- `PbKey`, `IftttKey`, `PoUserKey`, `BashScript`, `JsonUrl`, `Apprise` (notification channels)
- `UiBaseUrl`, `ExtrasSub`

### 1.4 — Add SignalR hub for real-time notifications
- Create `/hubs/notifications` SignalR hub
- Wire `NotificationService` to broadcast via SignalR
- Register in DI: `builder.Services.AddSignalR()`, `app.MapHub<NotificationHub>("/hubs/notifications")`
- Remove from AGENTS.md claim until implemented

### 1.5 — Add health check endpoint
- `GET /api/health` → `{ "status": "healthy", "timestamp": "...", "version": "..." }`
- Useful for Docker health checks and orchestration

---

## Phase 2: Web UI Feature Parity

### 2.1 — Add missing controllers + views (9 new pages)

| Controller | Route | View | ARM Equivalent |
|-----------|-------|------|----------------|
| `LogsController` | `/logs` | `Views/Logs/Index.cshtml` | `listlogs` |
| | `/logs/view` | `Views/Logs/Viewer.cshtml` | `logview` (tail/full/arm/download) |
| `DatabaseController` | `/database` | `Views/Database/Index.cshtml` | `databaseview` (pagination, search) |
| | `/database/update` | `Views/Database/Update.cshtml` | `databaseupdate` (migrate DB) |
| | `/database/import` | — | `import_movies` (scan completed path) |
| `SettingsController` | `/settings` | Expand tab view | 7-tab settings (general, sysinfo, ripper, UI, abcde, apprise, help) |
| | `/settings/system` | `Views/Settings/SystemInfo.cshtml` | `sysinfo` (CPU/mem/storage/HW transcode) |
| | `/settings/eject` | — | Drive eject action |
| `ApiController` | `/api/abandon/{id}` | — | Kill job process + eject |
| | `/api/change-params` | — | Change rip params mid-job |
| | `/api/log` | — | Parse log files for progress |

### 2.2 — Enhance existing views

| View | Enhancement | ARM Reference |
|------|-------------|---------------|
| `Home/Index.cshtml` | Add server stats (CPU, memory, storage, HW transcode) | `index.html` + `sysinfo.html` |
| | Add AJAX auto-refresh of active jobs | `jobRefresh.js` |
| `Jobs/JobDetail.cshtml` | Add poster display | `jobdetail.html` |
| | Add metadata (ratings, plot toggle) | |
| | Add job config snapshot display | |
| | Add manual edit controls (title, params) | |
| | Add track editing (main feature, process) | |
| `Jobs/TitleSearch.cshtml` | Add result grid with posters | `list_titles.html` |
| | Add custom title form | `customTitle.html` |
| | Add change params form | `changeparams.html` |
| `Settings/Index.cshtml` | Change to 7-tab layout | `settings.html` |
| | Add ripper config form | `ripper.html` |
| | Add UI settings tab | `ui.html` |
| | Add abcde config editor | `abcde.html` |
| | Add Apprise config editor | `apprise.html` |
| | Add system info tab | `tab_sysinfo.html` |
| `History/Index.cshtml` | Add pagination | `pagination.html` |
| | Add sortable columns | tablesorter |
| `Notifications/Index.cshtml` | Add bell badge in nav | `navnotify.html` |
| `Shared/_Layout.cshtml` | Add nav items: Logs, Database | `nav.html` |
| | Add notification bell badge | |

### 2.3 — Client-side assets
- **Current:** SimpleCSS from CDN
- **Target:** Bootstrap 4 (matches ARM), jQuery, tablesorter, dark mode toggle
- Serve from CDN with integrity hashes (or bundle later)
- Create `wwwroot/js/common.js` and `wwwroot/js/jobRefresh.js`

### 2.4 — Drive management
- Implement `/settings/scan` to scan udev for optical drives
- Add eject button per drive
- Add drive mode toggle (auto/manual)
- Reference: `arm/ui/settings/DriveUtils.py`

---

## Phase 3: Production Docker + Tarantino Devcontainer

### 3.1 — Refactor Dockerfile to use ARM base image
- **Current:** `mcr.microsoft.com/dotnet/aspnet:10.0` + apt-get install tools
- **Target:** `FROM automaticrippingmachine/arm-dependencies:1.7.3 AS base` then add .NET runtime
- Keeps us as a drop-in replacement — same udev rules, same mount points, same service structure
- May need to add .NET runtime from Microsoft feed onto the Phusion baseimage

Dockerfile structure:
```dockerfile
# Build stage — use full .NET SDK
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
...

# Runtime stage — use ARM base with .NET runtime added
FROM automaticrippingmachine/arm-dependencies:1.7.3 AS runtime
# Install .NET runtime on Phusion baseimage
COPY --from=build /app/cli /app/cli
COPY --from=build /app/webui /app/webui
# Keep ARM's udev rules, mount points, runit services
COPY docker-entrypoint.sh /usr/local/bin/
ENTRYPOINT ["docker-entrypoint.sh"]
```

### 3.2 — Tarantino devcontainer profile
- File: `.devcontainer/tarantino/devcontainer.json`
- Base: Custom Dockerfile that starts from `arm-dependencies` and installs .NET SDK
- Run args for hardware access:

```json
{
    "name": "ARM .NET — Tarantino",
    "build": { "dockerfile": "Dockerfile" },
    "runArgs": [
        "--privileged",
        "--gpus", "all",
        "-v", "/opt/arm:/opt/arm",
        "-v", "/etc/arm/config:/etc/arm/config"
    ],
    "forwardPorts": [8080],
    "customizations": {
        "vscode": {
            "extensions": ["ms-dotnettools.csdevkit", ...]
        }
    }
}
```

- Devcontainer Dockerfile: `arm-dependencies` base + .NET 10 SDK from Microsoft

### 3.3 — GPU passthrough documentation
- NVIDIA: `--gpus all`, needs `nvidia-container-toolkit` on host
- Intel QSV: `--device /dev/dri:/dev/dri`
- AMD VAAPI: `--device /dev/dri:/dev/dri`
- Add `--device` entries for `/dev/sr*` (or use `--privileged`)

### 3.4 — Mount point conventions (match ARM)
- `/dev/sr*` → `/mnt/dev/sr*` (fstab entries)
- `/opt/arm/raw` — raw rips
- `/opt/arm/transcode` — transcoding work
- `/opt/arm/completed` — final output
- `/opt/arm/logs` — log files
- `/etc/arm/config` — config (arm.yaml, arm.db, abcde.conf, apprise.yaml)

---

## Phase 4: Hardware Testing on Tarantino

### 4.1 — DVD pipeline test
- Insert known DVD
- Run `dotnet run --project src/ArmRipper.Cli -- --device /dev/sr0`
- Verify: Identify → CRC64 lookup → MakeMKV rip → HandBrake transcode → file move → notify
- Test both main feature and full disc modes

### 4.2 — Blu-ray pipeline test
- Insert known Blu-ray
- Same pipeline with MakeMKV (mandatory for Blu-ray)
- Verify BD-ROM identification via bdmt_eng.xml

### 4.3 — Audio CD test
- Requires abcde with proper config
- Test MusicBrainz disc ID + track listing + rip

### 4.4 — Data disc test
- Verify dd-based data rip

### 4.5 — Web UI test
- Open http://tarantino:8080
- Test: dashboard, job detail, history, logs, settings, database
- Test with active rip for real-time updates

### 4.6 — Error recovery tests
- Insert dirty/scratched disc
- Kill process mid-rip
- Pull disc out mid-operation
- Test duplicate detection

---

## Phase 5: OSS Polish

### 5.1 — Authentication (optional)
- Add ASP.NET Core Identity or simple bcrypt auth if desired
- Protect routes behind `[Authorize]` attributes

### 5.2 — CI/CD pipeline
- GitHub Actions: `dotnet build` + `dotnet test` on PR
- Docker build + push to Docker Hub on tag
- Multi-arch builds (linux/amd64, linux/arm64)

### 5.3 — Docker Hub publish
- `docker buildx build --platform linux/amd64,linux/arm64 ...`
- Push to `yourorg/arm-sharp:latest`
- Tags for versions

### 5.4 — README + contribution guide
- Project overview
- Quick start (Docker)
- Development setup
- Configuration reference
- FAQ / troubleshooting

### 5.5 — Unit test expansion
- Add more edge case coverage
- Integration tests with mock devices
- Test all new views and controller endpoints

---

## Immediate Next Steps (What to do first)

1. **Create Tarantino devcontainer** — so you can develop directly on the target hardware
2. **Fix CRC64** — without this, DVD identification is broken
3. **Register WebUi services** — so the web UI can trigger/display real rip data
4. **Add SignalR hub** — real-time notifications during development
5. **Start adding missing views** — logs viewer is the most useful for debugging

Start with steps 1-2 in parallel. Step 1 unlocks real hardware testing; step 2 unlocks the DVD pipeline.
