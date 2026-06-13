# ARM .NET Port — Development Plan

## Strategy
- **Parity-first**: Match all original ARM Web UI features, then test hardware
- **Drop-in Docker**: Container matches ARM's volume/device/port conventions
- **OSS-ready**: Decisions documented, no proprietary deps

## Architecture Decisions

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

## Completed

All planned phases are implemented:
- **Phase 0-2:** Foundation, runtime gaps, Web UI feature parity
- **Phase 4:** Hardware testing on Tarantino (DVD, BD, Audio CD, Data disc)
- **Phase 5:** Authentication (cookie auth, `[Authorize]`, `DisableLogin`, 6 integration tests)
- **Phase 6:** Disc metadata caching (fingerprint, 3 DB tables, SINFO expansion, cache flow)
- **Phase 7:** All 9 runtime gaps resolved (MakeMkvService tests, integration tests, scope leak fix, etc.)

See `ARCHITECTURE.md` for full project structure and pipeline details.

---

## Phase 3: Production Docker + Tarantino Devcontainer

### 3.1 — Refactor Dockerfile to use ARM base image
- **Target:** `FROM automaticrippingmachine/arm-dependencies:1.7.3 AS base` then add .NET runtime
- Keep as drop-in replacement — same udev rules, mount points, service structure

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
