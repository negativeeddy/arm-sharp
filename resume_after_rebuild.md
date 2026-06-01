# Resume After Devcontainer Rebuild

## Current State (May 31, after disk space recovery)

### Build Status
- `dotnet build` — 0 warnings, 0 errors (4 projects)
- `dotnet test` — 51/51 passing (all tests in ~2s)
- Last commit: `d02f37f` — "Extract ICliProcessRunner interface, fix test hang on Music/Data disc tests"

### What Was Fixed This Session

**`ICliProcessRunner` interface + mock fix (rebuild hang):**
- Test hang after devcontainer rebuild: `ConductorTests` for Music and Data disc types shelled out to real hardware (`abcde`, `dd` on `/dev/sr0`) — no disc loaded → hung indefinitely.
- Extracted `ICliProcessRunner` interface from `CliProcessRunner` + removed `sealed`.
- Updated all 10 services/controllers to depend on `ICliProcessRunner`.
- Updated DI registrations in CLI and WebUI to `AddSingleton<ICliProcessRunner, CliProcessRunner>()`.
- `ConductorTests.CreateConductor` now defaults to a mocked runner that returns success immediately.

## Hardware State (Tarantino)
- **Drives:** `/dev/sr0` (BD-RE BU40N), `/dev/sr1` (DRW-24B1ST) — both accessible, no discs loaded
- **CLI tools:** HandBrakeCLI, makemkvcon, ffmpeg, ffprobe, abcde, eject — all installed
- **Paths:** `/opt/arm/{raw,transcode,completed,logs}` — exist, empty
- **Config:** `/etc/arm/config/arm.yaml` — present
- **Note:** Host ran out of disk space (Docker images on small drive); may need to move Docker storage before continuing

## Next Steps (from PLAN.md)

The highest-impact items in priority order:

### 1. Fix CRC64 (Phase 1.1)
- **Problem:** `IdentifyService.cs:171-180` returns `"0000000000000000"` — DVD identification via ARM API always fails.
- **Target:** Port `pydvdid` CRC64 algorithm from Python (reference at `/workspaces/automatic-ripping-machine`).
- **Alternative:** Call `makemkvcon` for disc ID as fallback.

### 2. Register WebUi services (Phase 1.2)
- WebUi only registers `ArmDbContext` — no ripping services available.
- Register all core services + add `appsettings.json`.

### 3. Add SignalR hub (Phase 1.4)
- Wire `NotificationService` to broadcast via SignalR for real-time UI updates.

### 4. Start adding missing views (Phase 2)
- Logs viewer is the most useful for debugging during hardware testing.

## Files to Resume From
- `docs/AGENTS.md` — project status overview
- `docs/PLAN.md` — development plan by phase
- `docs/IMPROVEMENTS.md` — deferred polish and refactoring notes
- `resume_after_rebuild.md` — this file
