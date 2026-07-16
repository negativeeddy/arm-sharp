# Improvements & Refactoring Notes

Track usability, DX, and architecture improvements. Focus: user-friendliness, easy setup, easy diagnosis.

## Gitignore
- DB file `data/arm.db` is not gitignored — should be added so test/seed data isn't committed.

## Data / Persistence
- **Migrate from `DateTime` to `DateTimeOffset`** — All model timestamps (`Job.StartTime`, `Job.StopTime`, `Notification.Timestamp`, `DiscMetadata.CreatedAt`/`LastUsedAt`, etc.) use `DateTime`. EF Core reads these back as `DateTimeKind.Unspecified` from SQLite, losing the UTC context. `.ToLocalTime()` in views works but is a band-aid. Switching to `DateTimeOffset` stores the offset with the value and makes timezone intent explicit. Requires touching models, EF mappings, all view comparisons, and serialization.

## Configuration & Setup
- **Add 4K UHD disc type with separate settings** — Currently `ArmSettings` and `ConfigSnapshot` only have `HbArgsDvd` / `HbPresetDvd` and `HbArgsBd` / `HbPresetBd`. 4K UHD discs need different handling (ffmpeg passthrough to preserve HDR, no HandBrake re-encode). Add `HbArgsUhd` / `HbPresetUhd` and `FfmpegPostFileArgsUhd` properties, a `DiscType.Uhd` enum value, and wire them through `HandBrakeService.GetHbSettings()` and the `Conductor` pipeline. For 4K UHD, the typical workflow is `USE_FFMPEG: true` with `-c:v copy -c:a ac3 -b:a 640k` to preserve HDR metadata losslessly. Users currently have to swap `arm.yaml` manually when switching between 1080p and 4K discs — automatic disc-type detection would eliminate this.
- No `appsettings.Production.json` or env-aware config profiles. Would help users separate sensitive keys (API keys) from path config.
- Seed data scripts exist in `scripts/` but require manual run. Consider auto-seeding on first launch for demo/testing.

## UI / User Experience
- **Restart from last successful stage** — add a "retry from failure" action that resumes the pipeline at the last failed stage instead of restarting from scratch. Requires each stage to checkpoint its completion state in the DB (e.g. a `Stages` table or bitfield on `Job`).

## SignalR
- `SignalRNotificationBroadcaster` is wired via `INotificationBroadcaster` interface. Works but the broadcaster is a singleton while the hub context is scoped per connection. Should verify no lifetime issues.

## Pages / Views
- **Redesign Identification section** — improve the layout of the Identification section on the Job detail page.
- **DVD/Blu-ray detection workflow** — the Settings page has a "Detect Disc" / "Scan Drives" button but the actual udev-based monitoring workflow from the original ARM isn't replicated. Should add a "Start Monitoring" action that runs the Conductor/IdentifyService loop.
- **Home dashboard** — core metrics displayed. Could add charts (job success rate over time, rips per day) or sparkline trends.
- Batch actions on Active Rips page (abandon all, retry all).

## MusicBrainz
- **Investigate moving off XML where possible** — MusicBrainz XML parsing is fragile (manual XElement traversal, namespace handling). If MusicBrainz offers a JSON endpoint, prefer it.

## Dependency Injection
- WebUi now has full DI wiring. However many services are registered as `Scoped` when they're effectively stateless. `CliProcessRunner` is singleton. Review lifetime choices — some could be singletons or transient.

## Startup & Recovery
- **Resume in-progress rips on restart** — currently, if the app is restarted while a job is ripping (VideoRipping, TranscodeActive, etc.), the background task is lost and the job stays stuck. On startup, scan for jobs in non-terminal states and resume them: re-attach the MakeMKV/HandBrake process if still running, or restart the rip/transcode stage from where it left off. Requires stage-level checkpointing (which stage completed, which files were produced) so the system can pick up without re-doing completed work.

## Testing
- **Audio CD test:** Deferred — low priority. Needs abcde conf and audio disc in drive.
- **Data disc test:** Deferred — needs data disc for testing.
- **Error recovery tests:** Deferred — needs dirty/scratched discs for edge case testing.
- No CRC64 test with real DVD data (uses synthetic directory).
- No SignalR hub tests.

## Security
- `LogsController.Reader` uses `Path.GetFileName` for sanitization but could use `Path.GetFullPath` + prefix validation as defense-in-depth.

## MCP (Model Context Protocol)
- ✅ **MCP server implemented** using the C# SDK (`ModelContextProtocol.AspNetCore` v1.4.1) with HTTP (Streamable HTTP) transport at `/mcp`.
- Exposed tools:
  - `get_jobs` — list jobs with optional status filter, `offset`, and `limit` pagination.
  - `get_logs` — read job log files with `offset` (line number) and `pageSize` for efficient browsing of long logs.
  - `get_config` — returns current ARM Sharp configuration (API key presence is shown as booleans, values are never exposed).
- 🔲 Future tools: `update_config`, `eject_drive`, `trigger_identify`, log streaming via SSE.

## Container / Deployment
- Docker image is ~2GB with full .NET SDK. Switch to self-contained publish with runtime-only image to reduce size.
- GitHub Actions CI has QEMU set up but only builds `linux/amd64`. Add `linux/arm64` multi-arch build once ARM64 runners or cross-compilation are available.
- **HandBrake nvdec support** — current `arm-dependencies:1.7.3` base image compiles HandBrake without `--enable-nvdec`. The devcontainer has a custom rebuild with nvdec working, but the production Dockerfile still uses the base image's build (no hw-decoding). Need to either fork and rebuild `arm-dependencies`, or add a multi-stage HandBrake build step to the production Dockerfile.
- Docker buildx warning — migrate from legacy builder to BuildKit.

## Disc Databases (Track Identification)
- **thediscdb.com integration** — Encrypted BDs often return 0 tracks from `makemkvcon info --robot`. thediscdb.com stores disc IDs mapped to known track layouts. Adding a lookup step would let us skip the expensive `makemkvcon info` scan for known discs and identify the correct main feature track without guessing by filesize. API is simple REST — define a `DiscDatabaseService` client, cache results locally, and plug into `IdentifyService`.

## Notifications (Low Priority)
Pushbullet, IFTTT, JSON webhook, and Bash script notifications are already implemented in `NotificationService.SendRemoteNotificationsAsync()`. Two additional channels remain:

### Pushover
- **API:** `POST https://api.pushover.net/1/messages.json` with `token` (app key), `user` (user key), `message`, `title`, `sound`, etc.
- **Config keys:** `PoUserKey` / `PO_USER_KEY` already exist in `ArmSettings` and `ConfigSnapshot`, mapped from YAML. Missing: a `PoAppToken` key for the application token.
- **Implementation:** ~20 lines in `NotificationService` — `SendPushoverAsync(client, appToken, userKey, title, body, ct)`.
- **Settings UI:** Apprise tab currently read-only; would need editable form fields.

### Apprise
- **CLI:** `apprise` is a command-line tool supporting 80+ notification services (Slack, Discord, Telegram, email, etc.). Original Python ARM invokes it via subprocess.
- **Config key:** `Apprise` / `APPRISE` already exist in `ArmSettings` and `ConfigSnapshot`.
- **Implementation:** ~30 lines in `NotificationService` — call `apprise -b "body" -t "title"` via `CliProcessRunner`.
- **Settings UI:** Same as Pushover — needs editable form on Apprise tab.
