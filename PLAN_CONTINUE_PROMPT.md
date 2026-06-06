# ARM-Sharp Continue Prompt

## Goal
Port automatic-ripping-machine Python ARM to C# .NET 10 as a drop-in Docker replacement with feature parity.

## Last State
- DVD pipeline tested end-to-end: mount → identify → CRC64 → MakeMKV rip (6 tracks) → HandBrake transcode (5/6) → move → cleanup. Exit 0.
- Blu-ray pipeline tested: mount → identify as BD → label "The Matrix" → test mode skips slow MakeMKV rip. Exit ~30s.
- 52/52 tests pass, build 0 warnings 0 errors.

## Phase 3 Completed
1. **YAML config provider** — `src/ArmRipper.Core/Configuration/ArmYamlConfigLoader.cs` maps 50+ arm.yaml UPPER_CASE keys to `Arm:CamelCase` config keys. Wired into both CLI and WebUI Program.cs.
2. **Production Dockerfile** — Rewritten to use `automaticrippingmachine/arm-dependencies:1.7.3` as runtime base + .NET runtime via Microsoft install script.
3. **Docker-in-Docker** — Added `docker.io` package to tarantino devcontainer Dockerfile and mounted `/var/run/docker.sock` in devcontainer.json.

## Next: Phase 5 — OSS Polish
- CI/CD pipeline (GitHub Actions for build + test + Docker build)
- Auth for Web UI
- README documentation
- Verify production Docker image actually builds and runs

## Key Context
- Running in Tarantino devcontainer (Phusion baseimage via `arm-dependencies:1.7.3`, --privileged, GPU passthrough)
- Drives: `/dev/sr0` (BD-RE BU40N, The Matrix BD), `/dev/sr1` (DRW-24B1ST, Schoolhouse_Rock_Disc2 DVD)
- NVIDIA GTX 1060, all CLI tools installed (HandBrakeCLI, makemkvcon, ffmpeg, ffprobe, abcde, eject)
- arm.yaml at `/etc/arm/config/arm.yaml` — now read natively by YAML config loader
- Config via `appsettings.json` + YAML overlay via `AddInMemoryCollection`

## Files Changed in Last Session
- `src/ArmRipper.Core/Configuration/ArmYamlConfigLoader.cs` — new file
- `Dockerfile` — rewritten for arm-dependencies base
- `src/ArmRipper.Cli/Program.cs` — added YAML loader + Microsoft.Extensions.Configuration using
- `src/ArmRipper.WebUi/Program.cs` — added YAML loader
- `.devcontainer/tarantino/Dockerfile` — added docker.io package
- `.devcontainer/tarantino/devcontainer.json` — added docker socket mount
