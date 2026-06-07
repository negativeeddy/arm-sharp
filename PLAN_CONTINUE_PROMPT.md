# ARM-Sharp Continue Prompt

## Goal
Port automatic-ripping-machine Python ARM to C# .NET 10 as a drop-in Docker replacement with feature parity.

## Current State — Pipeline Completeness

### Implemented & Tested (Pipeline Core)
1. **Disc identification** — `IdentifyService.cs`: mount, blkid label, DiscType (DVD/BD/Music/Data), DVD CRC64 lookup, BD title, OMDB/TMDB metadata. DVD tested end-to-end.
2. **MakeMKV rip** — `MakeMkvService.cs`: beta key auto-update, track info parsing, single-track rip, rip-all-titles, encrypted BD fallback.
3. **Transcode** — `HandBrakeService.cs` + `FfmpegService.cs`: main-feature, all-titles, mkv modes. Builds correct HandBrakeCLI/ffmpeg commands with config presets.
4. **Post-process** — `ArmRipperService.cs`: file moves, permissions, Emby scan, raw cleanup, notifications.
5. **Conductor** — `Conductor.cs`: orchestrates full workflow (setup → identify → rip → transcode → move → cleanup).
6. **YAML config** — `ArmYamlConfigLoader.cs`: reads arm.yaml into `ArmSettings`.
7. **Web UI** — `ArmRipper.WebUi`: API, auth, history, jobs, logs, settings, notifications controllers. SignalR hub.
8. **Dockerfile** — Multi-stage build with HW_ACCEL arg (nvidia/intel/amd/none), .NET 10 runtime install, entrypoint dispatches to cli/webui.
9. **45/46 tests pass** (1 WebUI integration test fails due to test infra, not code).

### Missing for "Insert Disc → Ripped Output"
1. **❌ Disc hotplug/udev monitoring** — No udev rules or polling daemon. Must invoke `docker run <image> cli --device /dev/sr0` manually. Original ARM uses `/etc/udev/rules.d/arm.rules` to auto-trigger on disc insert. NOT PORTED.
2. **❌ Docker daemon mode** — Entrypoint only dispatches CLI or WebUI. No background service that watches for discs. WebUI runs standalone but can't trigger rips without CLI invocation.
3. **🟡 Docker build verification** — Syntax validated, partial build attempted. Not yet fully built and end-to-end tested with GPU passthrough.

### What's Left: Phase 5 — OSS Polish
- **P0: udev/disc monitoring** — Port ARM's udev rule + create a daemon mode that watches `/dev/sr*` and auto-launches the CLI pipeline.
- **P1: Docker image verification** — Full `docker build` + `docker run` test with `--privileged` and device passthrough.
- **P2: CI/CD** — GitHub Actions for build + test + Docker build (`.github/workflows/`).
- **P3: Web UI auth** — Login page, session management, API key auth.
- **P4: README docs** — Setup, config, usage documentation.

## Key Context
- Running in Tarantino devcontainer (Phusion baseimage via `arm-dependencies:1.7.3`, --privileged, GPU passthrough)
- Drives: `/dev/sr0` (BD-RE BU40N), `/dev/sr1` (DRW-24B1ST DVD)
- NVIDIA GTX 1060, all CLI tools installed (HandBrakeCLI, makemkvcon, ffmpeg, ffprobe, abcde, eject)
- arm.yaml at `/etc/arm/config/arm.yaml` — read natively by YAML config loader
- Config via `appsettings.json` + YAML overlay via `AddInMemoryCollection`

## Files Changed in Last Session
- `src/ArmRipper.Core/Configuration/ArmYamlConfigLoader.cs` — new file
- `Dockerfile` — rewritten for arm-dependencies base
- `src/ArmRipper.Cli/Program.cs` — added YAML loader
- `src/ArmRipper.WebUi/Program.cs` — added YAML loader
- `.devcontainer/tarantino/Dockerfile` — added docker.io package
- `.devcontainer/tarantino/devcontainer.json` — added docker socket mount
- `tests/ArmRipper.Core.Tests/` — in-memory SQLite, new unit tests
- `tests/ArmRipper.WebUi.Tests/` — 6 controller integration tests
