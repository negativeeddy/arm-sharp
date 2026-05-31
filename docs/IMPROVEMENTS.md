# Improvements & Refactoring Notes

Track usability, DX, and architecture improvements to apply after feature parity.
Focus: user-friendliness, easy setup, easy diagnosis.

## Gitignore

- `[Ll]og/` and `[Ll]ogs/` in `.gitignore` are too broad — they match any directory named Logs/ at any depth. Changed to `/[Ll]og/` and `/[Ll]ogs/` (root-anchored) so `Views/Logs/` is tracked.

## Configuration & Setup

- `ArmSettings` is missing many properties that `ConfigSnapshot` already defines (HbArgsDvd, HbArgsBd, FfmpegCli, notification channels, etc.). After parity, should consolidate into one source of truth.
- No `appsettings.Production.json` or env-aware config profiles. Would help users separate sensitive keys (API keys) from path config.

## UI / User Experience

- Currently uses SimpleCSS from CDN with no JS framework. ARM uses Bootstrap 4 + jQuery + tablesorter. Should bundle or vendor these for offline/reduced-network operation.
- Log viewer polls every 2s with no backoff or error handling on disconnect. Use SignalR for live log streaming instead of polling.
- No dark mode. ARM has one.
- Nav has no active-tab highlighting.
- No favicon or branding assets.
- Error pages are plain ASP.NET Core default — should add custom error views.

## SignalR

- `SignalRNotificationBroadcaster` is wired via `INotificationBroadcaster` interface. Works but the broadcaster is a singleton while the hub context is scoped per connection. Should verify no lifetime issues.
- `NotificationHub` is empty — consider adding client-callable methods (mark-as-read, subscribe to job events).
- No client-side SignalR JS yet — need `/js/signalr.min.js` or npm/bundled JS.

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

## Security

- No auth (intentional per PLAN, Phase 5). API endpoints like `/api/jobs`, `/logs/download` are unauthenticated.
- `LogsController.Reader` reads arbitrary files within the log directory — no path traversal protection besides `..` and `/` checks. Could use `Path.GetFullPath` + prefix validation as defense-in-depth.

## MCP (Model Context Protocol)

- Add MCP integration so the project can be queried/supervised by AI agents during development and debugging. MCP tools could expose log streaming, config editing, job management, and disc identification — making the system observable and controllable through AI assistants.
- MCP server could expose: `get_jobs`, `get_logs`, `update_config`, `eject_drive`, `trigger_identify` as tools.

## Container / Deployment

- `WebServer:Port` appsetting controls port but defaults to 8080 in `Program.cs`. Dockerfile should expose this and document env var override.
- Docker image is ~2GB with full .NET SDK. Switch to self-contained publish with runtime-only image to reduce size.
