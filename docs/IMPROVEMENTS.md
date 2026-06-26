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
- **Pipeline visualization** — show the full rip pipeline in the WebUI (mount → identify → MakeMKV rip → HandBrake transcode → file move → cleanup) with per-stage status indicators (pending, active, success, failure). Each job's detail page should render a pipeline view so users can see at a glance which stage succeeded or failed.
- **Restart from last successful stage** — add a "retry from failure" action that resumes the pipeline at the last failed stage instead of restarting from scratch. Requires each stage to checkpoint its completion state in the DB (e.g. a `Stages` table or bitfield on `Job`).
- Log viewer: use SignalR for live log streaming instead of polling. Tail logs view should auto-scroll to end on load.
- Dark mode visual polish incomplete — some elements (tables, cards, nav) don't fully invert or remain unreadable.
- Nav has no active-tab highlighting.
- No favicon or branding assets. Current footer links to the old ARM repo not to the new ARM# repo.
- Error pages are plain ASP.NET Core default — should add custom error views.
- **Link log files** — everywhere a log file path is displayed, make it a clickable link to view/download the log.
- **Job status timestamps** — show both "job start time" and "current stage start time" on job detail page.
- **Notifications "mark all read"** — add a button to mark all notifications as read.
- **Settings tooltips** — all settings on the settings page should have an (i) indicator for tooltip/popup descriptions of the field.
- **Progress bar in WebUI** — MakeMKV outputs `PRGV:title,current,total` progress lines during rip. Pipe them via SignalR so users see a % progress bar in the UI.
- Memory and Storage indicators for full/free should show a bar graph showing the % full.
### Polish Items (not yet started)

## SignalR
- `SignalRNotificationBroadcaster` is wired via `INotificationBroadcaster` interface. Works but the broadcaster is a singleton while the hub context is scoped per connection. Should verify no lifetime issues.
- `NotificationHub` is empty — consider adding client-callable methods (mark-as-read, subscribe to job events).
- No connection status indicator in the UI. If SignalR disconnects, users get no feedback.
- Toast notifications not yet implemented — badge count updates but no popup.

## Pages / Views
- **Title search** — add title search similar to the Python ARM's search functionality.
- **Redesign Identification section** — improve the layout of the Identification section on the Job detail page.
- **DVD/Blu-ray detection workflow** — the Settings page has a "Detect Disc" / "Scan Drives" button but the actual udev-based monitoring workflow from the original ARM isn't replicated. Should add a "Start Monitoring" action that runs the Conductor/IdentifyService loop.
- **Logs viewer page** (`/logs`) — currently lists log files with download/view links. Consider adding inline log viewer with search, follow-tail, and SignalR live streaming.
- **Active Rips page** (`/jobs/activerips`) — separate page from the Home dashboard active-rips table. Could add batch actions (abandon all, retry all).
- **Database view** — search/filter/pagination works but could use column sorting by year/status/title.
- **Home dashboard** — core metrics displayed. Could add charts (job success rate over time, rips per day) or sparkline trends.
- External links — anywhere we reference an external id like an IMDB id, make that a link to the title page on the external site.

## MusicBrainz
- **Investigate moving off XML where possible** — MusicBrainz XML parsing is fragile (manual XElement traversal, namespace handling). If MusicBrainz offers a JSON endpoint, prefer it.

## HandBrakeService / FfmpegService
- **Inconsistent error handling:** HandBrakeService.TranscodeMkvAsync logs transcode failures and continues to the next file (never throws); FfmpegService.TranscodeMkvAsync throws on failure, aborting the entire job. One convention should win — either both continue on failure (best-effort for batch MKV transcodes) or both fail fast.

## Dependency Injection
- WebUi now has full DI wiring. However many services are registered as `Scoped` when they're effectively stateless. `CliProcessRunner` is singleton. Review lifetime choices — some could be singletons or transient.
- `OmdbService` and `TmdbService` use `AddHttpClient<T>()` — requires `Microsoft.Extensions.Http` which is implicitly available in ASP.NET Core but not declared in the project file. Should add explicit package reference.

## Startup & Recovery
- **Resume in-progress rips on restart** — currently, if the app is restarted while a job is ripping (VideoRipping, TranscodeActive, etc.), the background task is lost and the job stays stuck. On startup, scan for jobs in non-terminal states and resume them: re-attach the MakeMKV/HandBrake process if still running, or restart the rip/transcode stage from where it left off. Requires stage-level checkpointing (which stage completed, which files were produced) so the system can pick up without re-doing completed work.

## Testing
- **Audio CD test:** Deferred — low priority. Needs abcde conf and audio disc in drive.
- **Data disc test:** Deferred — needs data disc for testing.
- **Error recovery tests:** Deferred — needs dirty/scratched discs for edge case testing.
- No CRC64 test with real DVD data (uses synthetic directory).
- No SignalR hub tests.

## Security
- `LogsController.Reader` reads arbitrary files within the log directory — no path traversal protection besides `..` and `/` checks. Could use `Path.GetFullPath` + prefix validation as defense-in-depth.

## MCP (Model Context Protocol)
- Add MCP integration so the project can be queried/supervised by AI agents during development and debugging. MCP tools could expose log streaming, config editing, job management, and disc identification — making the system observable and controllable through AI assistants.
- MCP server could expose: `get_jobs`, `get_logs`, `update_config`, `eject_drive`, `trigger_identify` as tools.

## Container / Deployment
- `WebServer:Port` appsetting controls port but defaults to 8080 in `Program.cs`. Dockerfile should expose this and document env var override.
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
