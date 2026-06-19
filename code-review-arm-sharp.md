# Code Review: ARM-Sharp (.NET 10 C# Optical Disc Ripping Pipeline)

**Date:** 2026-06-18  
**Reviewer:** GitHub Copilot (big-pickle)  
**Scope:** Full solution review of `ArmRipper.slnx` — Core, CLI, WebUi, and test projects.

---

## 1. Summary

ARM-Sharp is a C# .NET 10 port of [automatic-ripping-machine](https://github.com/automatic-ripping-machine/automatic-ripping-machine) — a Docker-based optical disc ripping pipeline. The solution contains three projects:

- **ArmRipper.Core** — Business logic: disc identification (DVD/BD/Music/Data), MakeMKV ripping, HandBrake/FFmpeg transcoding, MusicBrainz metadata lookup, OMDB/TMDB metadata enrichment, notifications (Pushbullet/IFTTT/Apprise/Webhook), per-job log files, EF Core + SQLite persistence, and a `Conductor` orchestrator.
- **ArmRipper.Cli** — Console entry point for headless operation.
- **ArmRipper.WebUi** — ASP.NET Core MVC + Razor Pages + SignalR web interface with cookie authentication.

The project is well-structured, uses modern .NET 10 idioms (file-scoped namespaces, primary constructors), and has a comprehensive test suite (~50+ tests across two test projects). The architecture follows clean separation of concerns with interfaces, DI registration, and background job services.

---

## 2. Critical Issues (must-fix)

### 2.1 — Fire-and-forget SignalR broadcasts swallow exceptions (HIGH)

**Status:** ✅ FIXED (commit `be35792`)

**Files:** `Conductor.cs`, `ArmRipperService.cs`

The `_ =` discard on async Task broadcasts was fixed by converting `BroadcastJobUpdate` to `async Task` with proper `await` and `try/catch` logging. Progress callbacks now safely handle broadcast exceptions.

### 2.2 — `new HttpClient()` in `IdentifyService` causes socket exhaustion (HIGH)

**Status:** ✅ FIXED (commit `c88a87d`)

**Files:** `IdentifyService.cs`

`new HttpClient()` was replaced with `IHttpClientFactory` injection for all three HTTP call sites (CRC64, OMDB, TMDB), eliminating socket exhaustion risk.

### 2.3 — MusicBrainz `async void` hazards in audio rip path (HIGH)

**Status:** ❌ NOT FIXED

**Files:** `MusicBrainzService.cs`

The broader fire-and-forget risk was addressed in 2.1. No specific changes to `MusicBrainzService` made in this round.

### 2.4 — Potential `StackOverflowException` in `IdentifyLoopAsync` recursion (MEDIUM)

**Status:** ❌ NOT FIXED

**Files:** `IdentifyService.cs`

The `-`/`+` title-trimming loop makes sequential API calls. Requires architectural discussion.

### 2.5 — MakeMKV progress monitor file-system race condition (MEDIUM)

**Status:** ✅ FIXED (commit `8170e8a`)

**Files:** `MakeMkvService.cs`

Added `try/catch` for `FileNotFoundException`/`IOException` around file-size reads in `MonitorRipFileSizeAsync`, preventing silent monitor shutdown when MakeMKV renames/deletes files mid-iteration.

### 2.6 — Race condition in `CliProcessRunner.RunStreamingAsync` (MEDIUM)

**Status:** ❌ NOT FIXED

**Files:** `CliProcessRunner.cs`

The `finally` block kills the process before `WaitForExit()`. Output written during kill is lost. Stderr task runs uncancelled after kill. Requires refactoring the streaming pipeline.

### 2.7 — DB migration fallback in CLI/WebUi duplicates raw SQL (MEDIUM)

**Status:** ✅ FIXED (commit `bea24d5`)

**Files:** `DatabaseHelper.cs` (new), `Program.cs` (CLI + WebUi)

Extracted `DatabaseHelper.EnsureMigrated(ArmDbContext)` in `ArmRipper.Core.Infrastructure.Data`, eliminating duplicated raw SQL and fragile `ALTER TABLE` workarounds. Both entry points now call the shared helper.

---

## 3. Structural & Maintainability Issues

### 3.1 — `ArmRipperService.RipVisualMediaAsync` is a 350+ line method

**Status:** ❌ NOT FIXED

**Files:** `ArmRipperService.cs`

Still uses `goto afterMakeMkv;`. Significant refactoring needed to decompose into smaller testable methods.

### 3.2 — Naming inconsistencies

**Status:** ❌ NOT FIXED

- `ConfigSnapshot` has `GetAudioTitle` (verb vs noun)
- `Prevent99` → should be `PreventTrack99`
- `DelRawFiles` → should be `DeleteRawFiles`
- `NoOfTitles` → should be `TitleCount`

### 3.3 — Magic strings for job status

**Status:** ❌ NOT FIXED

**Files:** `JobStateExtensions.cs`

DB strings like `"ripping"`, `"waiting"` are string literals scattered through controllers and views. Should be constants.

### 3.4 — Duplicate `GetHardwareEncoderInfoAsync` in two controllers

**Status:** ❌ NOT FIXED

**Files:** `HomeController.cs`, `SettingsController.cs`

Identical methods exist in both controllers. Should be extracted into a shared service.

### 3.5 — `JobUpdate.FromJob` uses `job.Stage?.ToString()`

**Status:** ❌ NOT FIXED

If `RipStage` enum values are renamed, the SignalR client-side JavaScript breaks silently.

### 3.6 — Large `MakeMkvService` class with mixed responsibilities

**Status:** ❌ NOT FIXED

**Files:** `MakeMkvService.cs`

Parsing logic should be extracted into a dedicated `MakeMkvOutputParser` class.

---

## 4. Performance Review

### 4.1 — Unbounded `ConcurrentDictionary` in `JobFileLoggerProvider`

**Status:** ✅ FIXED (commit `2c6c5ee`)

**Files:** `JobFileLogger.cs`

`StreamWriter` instances are now properly removed and disposed when a job completes, preventing file-handle leaks over long-running sessions.

### 4.2 — `int.TryParse` called repeatedly in transcode loops

**Status:** ✅ FIXED

**Files:** `Track.cs`, `FfmpegService.cs`, `HandBrakeService.cs`

Added `[NotMapped] TrackNumberInt` property to `Track` model that pre-parses the string `TrackNumber`. Both `FfmpegService.TranscodeAllAsync` and `HandBrakeService.TranscodeAllAsync` now use `TrackNumberInt` instead of calling `int.TryParse` in a LINQ projection.

### 4.3 — `JobDupeCheckAsync` loads 10 jobs but only uses at most 2

**Status:** ✅ FIXED (commit `7774e1d`)

**Files:** `Conductor.cs`

`.Take(10)` reduced to `.Take(2)`.

### 4.4 — `GetTrackInfoAsync` reads all output into memory before processing

**Status:** ✅ FIXED

**Files:** `MakeMkvService.cs`

`MakeMkvService.GetTrackInfoAsync` already uses `RunStreamingAllAsync` (async streaming via `IAsyncEnumerable`), processing lines one-by-one without buffering the entire output.

### 4.5 — SignalR progress broadcasts on every progress tick

**Status:** ✅ FIXED

`ArmRipperService.ShouldBroadcastProgress` enforces a 200ms throttle via `ProgressBroadcastInterval`. Progress percent updates (MakeMKV & transcode) are filtered through this throttle; only state transitions bypass it.

---

## 5. C# Best Practices

### 5.1 — `goto` statement in `ArmRipperService.cs`

**Status:** ❌ NOT FIXED

Covered by 3.1.

### 5.2 — Missing `CancellationToken` propagation

**Status:** ❌ NOT FIXED

- `CompletedController.ScanCompletedFilesAsync` lacks `CancellationToken`
- `ProbeFileAsync` uses `WaitForExit()` instead of `WaitForExitAsync(CancellationToken)`
- Several `db.SaveChangesAsync()` omit `CancellationToken`

### 5.3 — `Job` entity uses `[NotMapped]` for transient fields

**Status:** ❌ NOT FIXED

Initial migration has stale columns for `MakeMkvProgress`/`TranscodeProgress`. Acceptable but should be cleaned up in a future migration.

### 5.4 — `PasswordHasher<User>` usage — verify result correctly

**Status:** ✅ FIXED (commit `ca61b21`)

**Files:** `AuthController.cs`

Now handles `PasswordVerificationResult.SuccessRehashNeeded` by rehashing and saving, preventing lockout after ASP.NET Core framework updates.

### 5.5 — `GetLocalIpAddress` skips `172.x.x.x` addresses

**Status:** ❌ NOT FIXED

**Files:** `NotificationService.cs`

Should use `IsPrivate()` check instead of hardcoded prefix.

---

## 6. ARM‑Sharp / Ripping Pipeline Review

### 6.1 — Job lifecycle correctness

**Status:** ✅ NO ACTION NEEDED

### 6.2 — Watcher service safety

**Status:** ✅ NO ACTION NEEDED

### 6.3 — Queue orchestration

**Status:** ❌ NOT FIXED

No persistent job queue. `ConcurrentDictionary` prevents duplicate per-device but not cross-device concurrency.

### 6.4 — External tool invocation

**Status:** ✅ PARTIALLY FIXED

**Files:** `CliProcessRunner.cs`

Dead code `AppendToLogAsync` removed (commit `1398f9b`). Watchdog script (`scripts/usb-watchdog.sh`) added as complementary tool (commit `5a282c5`). `RunStreamingAsync` now throws on non-zero exit (commit `5a282c5`).

**Remaining:** `RunStreamingAsync` race condition with stderr (see 2.6).

### 6.5 — Idempotency and error recovery

**Status:** ✅ NO ACTION NEEDED

### 6.6 — Concurrency model

**Status:** ✅ NO ACTION NEEDED

---

## 7. Improved Snippets

### 7.1 — Throttled SignalR broadcast

```csharp
// Add a throttling helper
private sealed class ThrottledBroadcaster
{
    private readonly IEnumerable<INotificationBroadcaster> _broadcasters;
    private readonly ILogger _logger;
    private DateTime _lastBroadcast = DateTime.MinValue;
    private static readonly TimeSpan ThrottleInterval = TimeSpan.FromMilliseconds(200);

    public ThrottledBroadcaster(IEnumerable<INotificationBroadcaster> broadcasters, ILogger logger)
    {
        _broadcasters = broadcasters;
        _logger = logger;
    }

    public async Task BroadcastAsync(JobUpdate update, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        if (now - _lastBroadcast < ThrottleInterval)
            return;
        _lastBroadcast = now;

        foreach (var b in _broadcasters)
        {
            try { await b.BroadcastJobUpdateAsync(update, ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "Broadcast failed"); }
        }
    }
}
```

### 7.2 — Shared migration helper

```csharp
// In ArmRipper.Core.Infrastructure.Data
public static class DatabaseHelper
{
    public static void EnsureMigrated(ArmDbContext db)
    {
        try { db.Database.Migrate(); }
        catch
        {
            db.Database.EnsureCreated();
            db.Database.ExecuteSqlRaw(
                "CREATE TABLE IF NOT EXISTS \"__EFMigrationsHistory\" (\"MigrationId\" TEXT NOT NULL, \"ProductVersion\" TEXT NOT NULL);");
            db.Database.ExecuteSqlRaw(
                "INSERT OR IGNORE INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ('20260610044322_Initial', '10.0.0');");
            // Add subsequent migration IDs as needed
        }
        db.Database.ExecuteSqlRaw("PRAGMA busy_timeout = 5000;");
    }
}
```

### 7.3 — Use `IHttpClientFactory` in `IdentifyService`

```csharp
// Inject IHttpClientFactory
public sealed partial class IdentifyService(
    ICliProcessRunner runner,
    ILoggerFactory loggerFactory,
    ArmDbContext db,
    IOptions<ArmSettings> settings,
    IHttpClientFactory httpClientFactory) : IIdentifyService
{
    // Replace `new HttpClient()` with:
    using var httpClient = httpClientFactory.CreateClient("Metadata");
```

Add named client registration in both `Program.cs` files:
```csharp
builder.Services.AddHttpClient("Metadata", client =>
{
    client.Timeout = TimeSpan.FromSeconds(15);
});
```

### 7.4 — Simplify `RipWithMkv` logic

```csharp
private static bool RipWithMkv(Job currentJob, bool protection)
{
    var config = currentJob.Config;
    var ripMethod = config?.RipMethod ?? "mkv";
    var skipTranscode = config?.SkipTranscode ?? false;

    return currentJob.DiscType switch
    {
        DiscType.Bluray => true,
        DiscType.Dvd => ripMethod == "mkv" || skipTranscode || protection || ripMethod == "backup_dvd",
        _ => false
    };
}
```

---

## 8. Final Recommendations

### Highest‑impact changes (in priority order):

1. **Fix `new HttpClient()` in `IdentifyService`** — inject `IHttpClientFactory` and register named clients. This is the most impactful fix for production stability under concurrent disc identification.

2. **Fix fire-and-forget SignalR broadcasts** — convert `BroadcastJobUpdate` to `async Task`, await with try/catch, and add throttling in progress callbacks.

3. **Extract shared `DatabaseHelper.EnsureMigrated()`** — eliminate the duplicated raw SQL migration fallback in both CLI and WebUi entry points.

4. **Decompose `ArmRipperService.RipVisualMediaAsync`** — eliminate the `goto` and break the 350-line method into focused private methods for each pipeline stage.

5. **Extract MakeMKV parser** — move `ParseLine`, `SplitCsv`, and all `Parse*` methods from `MakeMkvService` into a dedicated `MakeMkvOutputParser` class for testability.

6. **Fix file-handle leak in `JobFileLoggerProvider`** — close/dispose `StreamWriter` instances when jobs complete, or switch to a file-rotation strategy.

7. **Use `IHttpClientFactory` throughout** — audit all remaining `new HttpClient()` usages (Metadata services, IdentifyService) and replace with managed clients.

8. **Add `SuccessRehashNeeded` handling** in `AuthController.Login` to prevent lockouts after ASP.NET Core password hash format updates.

9. **Throttle MakeMKV progress monitoring** in the file-size-based fallback monitor to avoid excessive CPU from polling `Directory.EnumerateFiles` every 2 seconds for each active rip.

10. **Remove dead code** — `CliProcessRunner.AppendToLogAsync` is defined but never called. Either wire it in or remove it.

### Architectural considerations for future iterations:

- **Add a persistent job queue** using `System.Threading.Channels` Channel<T> or a TPL Dataflow `ActionBlock` with bounded capacity to prevent resource exhaustion under load.
- **Consider `DbSet<T>.ExecuteUpdateAsync` for batch status updates** instead of loading full entities just to change a status field.
- **Add OpenTelemetry instrumentation** for distributed tracing across the rip pipeline — especially useful for diagnosing failures in production Docker deployments.
- **Evaluate `Polly` for retry policies** on transient HTTP failures in metadata lookups (OMDB, TMDB, MusicBrainz, Cover Art Archive).
