# Improvements & Refactoring Notes

Track usability, DX, and architecture improvements to apply after feature parity.
Focus: user-friendliness, easy setup, easy diagnosis.

**Status:** All planned features are implemented (Home dashboard, JobDetail, TitleSearch, History pagination, SignalR client). Remaining items below are polish, cleanup, and edge-cases.

## Gitignore

- `[Ll]og/` and `[Ll]ogs/` in `.gitignore` are too broad — they match any directory named Logs/ at any depth. Changed to `/[Ll]og/` and `/[Ll]ogs/` (root-anchored) so `Views/Logs/` is tracked.
- DB file `data/arm.db` is not gitignored — should be added so test/seed data isn't committed.

## Configuration & Setup

- `ArmSettings` is missing many properties that `ConfigSnapshot` already defines (HbArgsDvd, HbArgsBd, FfmpegCli, notification channels, etc.). After parity, should consolidate into one source of truth.
- No `appsettings.Production.json` or env-aware config profiles. Would help users separate sensitive keys (API keys) from path config.
- Seed data scripts exist in `scripts/` but require manual run. Consider auto-seeding on first launch for demo/testing.

## UI / User Experience

- Currently uses SimpleCSS from CDN with no JS framework. ARM uses Bootstrap 4 + jQuery + tablesorter. Should bundle or vendor these for offline/reduced-network operation.
- **Pipeline visualization** — show the full rip pipeline in the WebUI (mount → identify → MakeMKV rip → HandBrake transcode → file move → cleanup) with per-stage status indicators (pending, active, success, failure). Each job's detail page should render a pipeline view so users can see at a glance which stage succeeded or failed. Specifics: left-to-right node graph showing completed/current/future stages, with current stage % progress and start/end times for each stage.
- **Restart from last successful stage** — add a "retry from failure" action that resumes the pipeline at the last failed stage instead of restarting from scratch. Requires each stage to checkpoint its completion state in the DB (e.g. a `Stages` table or bitfield on `Job`).
- Log viewer polls every 2s with no backoff or error handling on disconnect. Use SignalR for live log streaming instead of polling. Also: tail logs view should auto-scroll to end on load.
- Dark mode exists (`arm.toggleDarkMode` + CSS class) but visual polish incomplete — some elements (tables, cards, nav) don't fully invert or remain unreadable.
- Nav has no active-tab highlighting.
- No favicon or branding assets. ARM .NET Port needs proper name, link to GitHub repo, and eventually commit ID/version displayed in the UI.
- Error pages are plain ASP.NET Core default — should add custom error views.
- ~~No dark mode. ARM has one.~~ ✅ Implemented via CSS class toggle + localStorage.
- **Link log files** — everywhere a log file path is displayed, make it a clickable link to view/download the log.
- **Job status timestamps** — show both "job start time" and "current stage start time" on job detail page.
- **Notifications "mark all read"** — add a button to mark all notifications as read.
- **Settings tooltips** — all settings on the settings page should have an (i) indicator for tooltip/popup descriptions of the field.
- **Progress bar in WebUI** — MakeMKV outputs `PRGV:title,current,total` progress lines during rip. The C# `RunStreamingAsync` already yields these lines — pipe them via SignalR so users see a % progress bar in the UI.

### Polish Items (not yet started)

## SignalR

- `SignalRNotificationBroadcaster` is wired via `INotificationBroadcaster` interface. Works but the broadcaster is a singleton while the hub context is scoped per connection. Should verify no lifetime issues.
- `NotificationHub` is empty — consider adding client-callable methods (mark-as-read, subscribe to job events).
- ~~No client-side SignalR JS yet — need `/js/signalr.min.js` or npm/bundled JS.~~ ✅ Done — CDN-loaded `@microsoft/signalr@8`, connects in `common.js:arm.startSignalR()`.
- No connection status indicator in the UI. If SignalR disconnects, users get no feedback.
- Toast notifications not yet implemented — badge count updates but no popup.

## Pages / Views

- **Title search** — add title search similar to the Python ARM's search functionality.
- **Redesign Identification section** — improve the layout of the Identification section on the Job detail page.
- **DVD/Blu-ray detection workflow** — the Settings page has a "Detect Disc" / "Scan Drives" button but the actual udev-based monitoring workflow from the original ARM isn't replicated. Should add a "Start Monitoring" action that runs the Conductor/IdentifyService loop.
- **Logs viewer page** (`/logs`) — currently lists log files with download/view links. Consider adding inline log viewer with search, follow-tail, and SignalR live streaming.
- **Active Rips page** (`/jobs/activerips`) — separate page from the Home dashboard active-rips table. Currently just lists all active jobs. Could add batch actions (abandon all, retry all).
- **Database view** — search/filter/pagination works but could use column sorting by year/status/title. Tablesorter is applied but needs click-to-sort on headers.
- **Home dashboard** — core metrics displayed. Could add charts (job success rate over time, rips per day) or sparkline trends.

## MusicBrainz

- `MusicBrainzService` creates `new HttpClient()` directly (lines 73, 277) instead of using `IHttpClientFactory` or accepting `HttpClient` via DI. Refactor to match OmdbService/TmdbService pattern.
- `GetCdArtAsync` is fire-and-forget (`_ = GetCdArtAsync(...)`) — cover art failures are silently swallowed. Should be awaited or explicitly backgrounded with error logging.
- No unit tests for XML parsing logic (`CheckMusicBrainzData`, `ProcessDiscRelease`, `ProcessCdStub`, `ProcessTracks`). Private methods — would need refactoring to expose or test via reflection.
- **Investigate moving off XML where possible** — MusicBrainz XML parsing is fragile (manual XElement traversal, namespace handling). If MusicBrainz offers a JSON endpoint, prefer it. Also reduces boilerplate versus the heavy `XDocument` API.

## HandBrakeService / FfmpegService

- **Inconsistent error handling:** HandBrakeService.TranscodeMkvAsync logs transcode failures and continues to the next file (never throws); FfmpegService.TranscodeMkvAsync throws on failure, aborting the entire job. One convention should win — either both continue on failure (best-effort for batch MKV transcodes) or both fail fast.
- **~~Track creation from MakeMKV TInfo~~** ✅ **Implemented** — MakeMKV TINFO/SINFO lines are now parsed and persisted to the `disc_tracks`/`disc_track_streams` tables. Tracks are also returned as `Track` entities immediately (as the original ARM does), and cached by disc fingerprint for future runs. The old post-rip filesystem-based track matching still runs as a fallback for file-size/name tracking.

## CRC64 / DVD Identification

- `DvdCrc64.Compute()` uses synchronous file I/O wrapped in `Task.Run`. Fine for 64KB reads, but could be async for streaming reads on slow DVD media.
- Uses `LastWriteTimeUtc` for file timestamps. Python pydvdid uses `getctime` (inode change time). On DVD-ROM they're identical but on dev/test filesystems they may differ, causing test vs production CRC mismatches.

## Dependency Injection

- WebUi now has full DI wiring. However many services are registered as `Scoped` when they're effectively stateless. `CliProcessRunner` is singleton. Review lifetime choices — some could be singletons or transient.
- `OmdbService` and `TmdbService` use `AddHttpClient<T>()` — requires `Microsoft.Extensions.Http` which is implicitly available in ASP.NET Core but not declared in the project file. Should add explicit package reference.

## Testing

- **Audio CD test (Phase 4.3):** Deferred — low priority. Needs abcde conf and audio disc in drive.
- **Data disc test (Phase 4.4):** Deferred — needs data disc for testing.
- **Error recovery tests (Phase 4.6):** Deferred — needs dirty/scratched discs for edge case testing.

- No integration tests for controllers/views.
- No CRC64 test with real DVD data (uses synthetic directory).
- No SignalR hub tests.
- `IEnumerable<INotificationBroadcaster>` in NotificationService — tests pass `[]` (empty collection), which is fine but silently skips broadcast verification. Would benefit from a `MockNotificationBroadcaster`.

## Miscellaneous Dead Code

- ~~`MakeMkvService.HmsToSeconds` is a private helper declared but never called.~~ ✅ **Now in use** — `HmsToSeconds` is actively called in the TINFO parsing within `GetTrackInfoAsync`.
- `HandBrakeService` had an unused `_durationValuePattern` field + `DurationValuePatternGen()` method (identical to the `DurationPattern` fix) — removed in 888fab2.

## Security

- No auth (intentional per PLAN, Phase 5). API endpoints like `/api/jobs`, `/logs/download` are unauthenticated.
- `LogsController.Reader` reads arbitrary files within the log directory — no path traversal protection besides `..` and `/` checks. Could use `Path.GetFullPath` + prefix validation as defense-in-depth.

## MCP (Model Context Protocol)

- Add MCP integration so the project can be queried/supervised by AI agents during development and debugging. MCP tools could expose log streaming, config editing, job management, and disc identification — making the system observable and controllable through AI assistants.
- MCP server could expose: `get_jobs`, `get_logs`, `update_config`, `eject_drive`, `trigger_identify` as tools.

- GitHub Actions `setup-dotnet` with `dotnet-version: 10.0.x` may fail if .NET 10 SDK isn't in the runner tool cache. Pin to a specific build or use `global.json` if the SDK feed changes.

## Container / Deployment

- `WebServer:Port` appsetting controls port but defaults to 8080 in `Program.cs`. Dockerfile should expose this and document env var override.
- Docker image is ~2GB with full .NET SDK. Switch to self-contained publish with runtime-only image to reduce size.
- GitHub Actions CI has QEMU set up but only builds `linux/amd64`. Add `linux/arm64` multi-arch build once ARM64 runners or cross-compilation are available.
- **HandBrake nvdec support** — current `arm-dependencies:1.7.3` base image compiles HandBrake without `--enable-nvdec`. The devcontainer has a custom rebuild with nvdec working, but the production Dockerfile still uses the base image's build (no hw-decoding). Need to either:
  - Fork and rebuild `arm-dependencies` with `--enable-nvdec --enable-nvdec` (requires `nv-codec-headers`), or
  - Add a multi-stage HandBrake build step to the production Dockerfile (~30+ min build time)

## Disc Databases (Track Identification)

- **~~`GetTrackInfoAsync` BD parser~~** ✅ **Fixed** — SINFO parsing now captures all stream types (video, audio, subtitle). Stream metadata (language, codec, channels, forced flag, resolution) is persisted to the `disc_track_streams` table. Per-track accumulators were refactored into a `FinalizeTrack` helper and the `StreamAccum` record type.

- **Disc metadata caching** ✅ **Implemented** — Disc fingerprint (`{VolumeLabel}::{SectorCount}`) computed during identification. Cached track metadata is persisted in `disc_metadata`, `disc_tracks`, and `disc_track_streams` tables. The `GetTrackInfoWithCacheAsync` method checks the cache first, skipping `makemkvcon info` for known discs. Cache eviction (old entries) still needs a strategy.

- **thediscdb.com integration** — Encrypted BDs often return 0 tracks from `makemkvcon info --robot` because the scanning/parsing is slow and fragile. thediscdb.com stores disc IDs (volume label, CRC) mapped to known track layouts (main feature, chapters, durations, language streams). Adding a lookup step would let us:
  - Skip the expensive `makemkvcon info` scan for known discs entirely.
  - Identify the correct main feature track without guessing by filesize.
  - Pre-populate track metadata (aspect ratio, fps, audio languages, forced subtitle flags) for smarter HandBrake args.
  - Fall back to `makemkvcon` scanning only for discs not in the database.
  - API is simple REST — define a `DiscDatabaseService` client with disc-id → track-list response model, cache results locally, and plug into `IdentifyService` or a new `TrackIdentifyService` between identify and rip.
  - **Note:** The current disc fingerprinting and track caching provides a local-first foundation. thediscdb.com could be added as an upstream cache-population source later.
