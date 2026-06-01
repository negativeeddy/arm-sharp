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
- **Pipeline visualization** — show the full rip pipeline in the WebUI (mount → identify → MakeMKV rip → HandBrake transcode → file move → cleanup) with per-stage status indicators (pending, active, success, failure). Each job's detail page should render a pipeline view so users can see at a glance which stage succeeded or failed.
- **Restart from last successful stage** — add a "retry from failure" action that resumes the pipeline at the last failed stage instead of restarting from scratch. Requires each stage to checkpoint its completion state in the DB (e.g. a `Stages` table or bitfield on `Job`).
- Log viewer polls every 2s with no backoff or error handling on disconnect. Use SignalR for live log streaming instead of polling.
- Dark mode exists (`arm.toggleDarkMode` + CSS class) but visual polish incomplete — some elements (tables, cards, nav) don't fully invert or remain unreadable.
- Nav has no active-tab highlighting.
- No favicon or branding assets.
- Error pages are plain ASP.NET Core default — should add custom error views.
- ~~No dark mode. ARM has one.~~ ✅ Implemented via CSS class toggle + localStorage.

### Polish Items (not yet started)

- **SignalR toast notifications** — when a `Notification` arrives via SignalR, show a Bootstrap toast popup in the corner instead of just updating the badge count.
- **Dark mode polish** — verify all Bootstrap components invert correctly: `.table-striped`, `.card`, `.modal`, `.form-control`, `.btn-outline-*`. Some may need custom CSS overrides.
- **Responsive fixes** — test on 1024px and 320px widths. The stats cards row on Home may wrap poorly. The TitleSearch inline form may overflow its cell.
- **Error handling UX** — when `fetch()` API calls fail (network errors, 500s), show a dismissible alert or toast instead of silently failing. Currently most `fetch()` calls have empty `.catch(function () {})`.
- **Active nav highlighting** — add JS or server-side logic to set `.active` on the nav item matching the current route.

## SignalR

- `SignalRNotificationBroadcaster` is wired via `INotificationBroadcaster` interface. Works but the broadcaster is a singleton while the hub context is scoped per connection. Should verify no lifetime issues.
- `NotificationHub` is empty — consider adding client-callable methods (mark-as-read, subscribe to job events).
- ~~No client-side SignalR JS yet — need `/js/signalr.min.js` or npm/bundled JS.~~ ✅ Done — CDN-loaded `@microsoft/signalr@8`, connects in `common.js:arm.startSignalR()`.
- No connection status indicator in the UI. If SignalR disconnects, users get no feedback.
- Toast notifications not yet implemented — badge count updates but no popup.

## Pages / Views

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
- **Track creation from MakeMKV TInfo:** MakeMKV prints detailed track metadata (duration, chapters, stream info) via TInfo lines, but this data is collected into a local list and discarded. Tracks are instead created after the fact from filenames on disk. For progress tracking and richer metadata, the TInfo output should be parsed into Track entities as the original ARM does.

## CRC64 / DVD Identification

- `DvdCrc64.Compute()` uses synchronous file I/O wrapped in `Task.Run`. Fine for 64KB reads, but could be async for streaming reads on slow DVD media.
- Uses `LastWriteTimeUtc` for file timestamps. Python pydvdid uses `getctime` (inode change time). On DVD-ROM they're identical but on dev/test filesystems they may differ, causing test vs production CRC mismatches.

## Dependency Injection

- WebUi now has full DI wiring. However many services are registered as `Scoped` when they're effectively stateless. `CliProcessRunner` is singleton. Review lifetime choices — some could be singletons or transient.
- `OmdbService` and `TmdbService` use `AddHttpClient<T>()` — requires `Microsoft.Extensions.Http` which is implicitly available in ASP.NET Core but not declared in the project file. Should add explicit package reference.

## Testing

- No integration tests for controllers/views.
- No CRC64 test with real DVD data (uses synthetic directory).
- No SignalR hub tests.
- `IEnumerable<INotificationBroadcaster>` in NotificationService — tests pass `[]` (empty collection), which is fine but silently skips broadcast verification. Would benefit from a `MockNotificationBroadcaster`.

## Miscellaneous Dead Code

- `MakeMkvService.HmsToSeconds` is a private helper declared but never called. Keep as-is — it was likely intended for parsing MakeMKV duration output and may be needed when TInfo track persistence is implemented.
- `HandBrakeService` had an unused `_durationValuePattern` field + `DurationValuePatternGen()` method (identical to the `DurationPattern` fix) — removed in 888fab2.

## Security

- No auth (intentional per PLAN, Phase 5). API endpoints like `/api/jobs`, `/logs/download` are unauthenticated.
- `LogsController.Reader` reads arbitrary files within the log directory — no path traversal protection besides `..` and `/` checks. Could use `Path.GetFullPath` + prefix validation as defense-in-depth.

## MCP (Model Context Protocol)

- Add MCP integration so the project can be queried/supervised by AI agents during development and debugging. MCP tools could expose log streaming, config editing, job management, and disc identification — making the system observable and controllable through AI assistants.
- MCP server could expose: `get_jobs`, `get_logs`, `update_config`, `eject_drive`, `trigger_identify` as tools.

## Container / Deployment

- `WebServer:Port` appsetting controls port but defaults to 8080 in `Program.cs`. Dockerfile should expose this and document env var override.
- Docker image is ~2GB with full .NET SDK. Switch to self-contained publish with runtime-only image to reduce size.
