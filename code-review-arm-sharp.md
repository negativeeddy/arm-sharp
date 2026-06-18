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

**Files:** `Conductor.cs` line 24-27, `ArmRipperService.cs` line 65-71

```csharp
private void BroadcastJobUpdate(Job job)
{
    var update = JobUpdate.FromJob(job);
    foreach (var b in broadcasters)
        _ = b.BroadcastJobUpdateAsync(update);
}
```

The `_ =` discard on an async Task means:
- Exceptions from `BroadcastJobUpdateAsync` are silently swallowed.
- If the SignalR hub is disconnected, the broadcast fails silently.
- In `ArmRipperService.cs`, progress callbacks via `MkvProgress()` and `TranscodeProgress()` call `BroadcastJobUpdate(job)` on every progress tick — these are called many times per second, creating unobserved task exceptions that can trigger `TaskScheduler.UnobservedTaskException`.

**Fix:** Use `try/catch` with logging inside the loop, at minimum:

```csharp
private void BroadcastJobUpdate(Job job)
{
    var update = JobUpdate.FromJob(job);
    foreach (var b in broadcasters)
    {
        try { await b.BroadcastJobUpdateAsync(update); }
        catch (Exception ex) { logger.LogWarning(ex, "Broadcast failed"); }
    }
}
```

Make the method `async Task` and await the calls. For progress callbacks, consider throttling to avoid flooding SignalR.

### 2.2 — `new HttpClient()` in `IdentifyService` causes socket exhaustion (HIGH)

**Files:** `IdentifyService.cs` lines ~300, ~350, ~475

```csharp
using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
```

`IdentifyService` creates `new HttpClient()` in **three places** (CRC64 API call, OMDB detail lookup, and again in `OmdbSearchAsync`/`TmdbSearchAsync`). Despite other services using `IHttpClientFactory`, `IdentifyService` bypasses it entirely. Under load (multiple discs being identified concurrently), this can exhaust ephemeral ports and cause `System.Net.Sockets.SocketException`.

**Fix:** Inject `IHttpClientFactory` into `IdentifyService` and use named or typed clients for all HTTP calls. The service already receives `IOptions<ArmSettings>` — use those API keys with properly managed clients.

### 2.3 — MusicBrainz `async void` hazards in audio rip path (HIGH)

**Files:** `MusicBrainzService.cs` line ~200

```csharp
_ = await GetCdArtAsync(job, disc, ns, ct);
```

While this particular call is awaited, there is a fire-and-forget pattern risk. More critically, in `ProcessDiscRelease`, the `GetCdArtAsync` call is awaited but the method signature does not properly propagate cancellation or handle the case where the Cover Art Archive is unreachable (it does, but swallows exceptions silently). 

The more serious concern is the `_ =` pattern elsewhere in the codebase — see issue 2.1.

### 2.4 — Potential `StackOverflowException` in `IdentifyLoopAsync` recursion (MEDIUM)

**Files:** `IdentifyService.cs` lines ~410-435

```csharp
private async Task<JsonDocument?> IdentifyLoopAsync(Job job, string title, string year, CancellationToken ct)
{
    // ...
    while (response is null && title.Contains('-'))
    {
        title = title[..title.LastIndexOf('-')].TrimEnd('+');
        response = await CallMetadataProviderAsync(job, title, string.IsNullOrEmpty(year) ? null : year, ct);
    }

    while (response is null && title.Contains('+'))
    {
        title = title[..title.LastIndexOf('+')].TrimEnd('+');
        response = await CallMetadataProviderAsync(job, title, string.IsNullOrEmpty(year) ? null : year, ct);
        if (response is null)
            response = await CallMetadataProviderAsync(job, title, null, ct);
    }
```

While not recursive, this loop trims `title` by removing characters after `-` or `+`. If the title contains many such characters, it makes N+1 API calls sequentially. A title like `"This---Is---A---Test"` would make 4 API calls. This is wasteful and slow, not a crash risk — but combined with the `new HttpClient()` issue above, it multiplies socket pressure.

### 2.5 — MakeMKV progress monitor file-system race condition (MEDIUM)

**Files:** `MakeMkvService.cs` lines ~210-245

```csharp
foreach (var file in Directory.EnumerateFiles(outputPath, "*.mkv"))
{
    if (preExisting.Contains(file)) continue;
    totalSize += new FileInfo(file).Length;
}
```

The `MonitorRipFileSizeAsync` method enumerates `.mkv` files while MakeMKV is writing them. If a file is deleted or renamed by MakeMKV between `EnumerateFiles` and `new FileInfo(file).Length`, a `FileNotFoundException` will crash the monitor task. The exception is caught by the general `catch (Exception ex)` but the monitor silently stops.

**Fix:** Add `try/catch` around the file-size read:

```csharp
try { totalSize += new FileInfo(file).Length; }
catch (FileNotFoundException) { continue; }
catch (IOException) { continue; }
```

### 2.6 — Race condition in `CliProcessRunner.RunStreamingAsync` (MEDIUM)

**Files:** `CliProcessRunner.cs` lines ~120-175

```csharp
finally
{
    if (!process.HasExited)
    {
        try { process.Kill(entireProcessTree: true); } catch { }
    }
}

process.WaitForExit();
```

The `finally` block kills the process, then `WaitForExit()` is called after the process may already be killed. If the process writes output during the kill, that output is lost. Additionally, the `stderrTask` runs concurrently but is never cancelled if the process is killed — it will complete reading any buffered stderr.

### 2.7 — DB migration fallback in CLI/WebUi duplicates raw SQL (MEDIUM)

**Files:** `Program.cs` (CLI) lines ~55-75 and `Program.cs` (WebUi) lines ~60-80

Both entry points have nearly identical ~20-line blocks for fallback migration:
```csharp
try { db.Database.Migrate(); }
catch
{
    db.Database.EnsureCreated();
    db.Database.ExecuteSqlRaw("CREATE TABLE IF NOT EXISTS \"__EFMigrationsHistory\"...");
    try { db.Database.ExecuteSqlRaw("ALTER TABLE jobs ADD COLUMN Warnings TEXT NULL;"); } catch { }
    // ...
}
```

This duplicates logic and the raw SQL `ALTER TABLE` workaround is fragile. If migration history diverges between CLI and WebUi, the database schema will be inconsistent.

**Fix:** Extract a shared `DatabaseHelper.EnsureMigrated(ArmDbContext)` method in the Core project.

---

## 3. Structural & Maintainability Issues

### 3.1 — `ArmRipperService.RipVisualMediaAsync` is a 350+ line method

**Files:** `ArmRipperService.cs`

This method handles MakeMKV rip, test-mode trimming, transcode dispatch, file moves, cleanup, and notifications. It uses `goto afterMakeMkv;` — a label in modern C# that signals deep architectural issues. The method should be decomposed into smaller, testable private methods.

### 3.2 — Naming inconsistencies

- `ConfigSnapshot` has properties like `GetAudioTitle` (a verb) while others are nouns/settings. This is a configuration setting, not a method.
- `Prevent99` — unclear without context. Should be `PreventTrack99` or `PreventTitle99`.
- `DelRawFiles` — abbreviates "Delete". Use `DeleteRawFiles`.
- `NoOfTitles` — use `TitleCount` for clarity.

### 3.3 — Magic strings for job status

**Files:** `JobStateExtensions.cs`

```csharp
public static string ToDbString(this JobState state) => state switch
{
    JobState.Success => "success",
    JobState.Failure => "fail",
    // ...
};
```

The DB strings `"ripping"`, `"waiting"`, `"waiting_transcode"` are used as string literals throughout controllers and views. These should be constants on `JobState` or a dedicated static class.

### 3.4 — Duplicate `GetHardwareEncoderInfoAsync` in two controllers

Both `HomeController.cs` and `SettingsController.cs` have identical `GetHardwareEncoderInfoAsync`, `AddNvidiaEncoderAsync`, and `AddFfmpegEncodersAsync` methods. This should be extracted into a shared service or a base controller.

### 3.5 — `JobUpdate.FromJob` uses `job.Stage?.ToString()` which relies on `RipStage.ToString()`

If `RipStage` enum values are renamed, the SignalR client-side JavaScript will break silently because the UI matches on string values like `"Identify"`, `"Rip"`, `"Transcode"`, `"Done"`. Consider using explicit string constants or `[EnumMember]` attributes.

### 3.6 — Large `MakeMkvService` class with mixed responsibilities

`MakeMkvService` handles:
- MakeMKV key management (beta key fetch, registration)
- Output parsing (CSV splitting, line parsing)
- Track info retrieval and caching
- File-size monitoring for progress
- Track persistence to DB

The parsing logic (especially `SplitCsv`, `ParseLine`, and all `Parse*` methods) should be extracted into a dedicated `MakeMkvOutputParser` class for testability and separation of concerns.

---

## 4. Performance Review

### 4.1 — Unbounded `ConcurrentDictionary` in `JobFileLoggerProvider`

**Files:** `JobFileLoggerProvider.cs`

```csharp
private readonly ConcurrentDictionary<string, StreamWriter> _writers = new();
```

Each job gets a `StreamWriter` that stays open for the lifetime of the application. For a long-running ripping server that processes hundreds of discs, this leaks file handles. Writers should be closed/disposed when the job completes.

### 4.2 — `int.TryParse` called repeatedly in `FfmpegService` transcode loops

**Files:** `FfmpegService.cs` lines ~130-135

```csharp
var eligibleTracks = job.Tracks.Where(t =>
    int.TryParse(t.TrackNumber, out var trackNo) &&
    trackNo <= (job.NoOfTitles ?? 0) && ...).ToList();
```

This parses `TrackNumber` for every track in every transcode loop. If tracks are filtered multiple times, the parsing happens repeatedly. Pre-parse track numbers when tracks are created.

### 4.3 — `JobDupeCheckAsync` loads 10 jobs but only uses at most 2

**Files:** `Conductor.cs` lines ~100-125

```csharp
var previousRips = await db.Jobs
    .Where(j => j.Label == job.Label && j.Status == JobState.Success)
    .OrderByDescending(j => j.StopTime)
    .Select(j => new { j.Title, j.Year, j.HasNiceTitle, j.VideoType, j.PosterUrl })
    .Take(10)
    .ToListAsync(ct);
```

`.Take(10)` loads 10 records but the logic only distinguishes between 0, 1, and >1 results. Use `.Take(2)`.

### 4.4 — `GetTrackInfoAsync` reads all output into memory before processing

**Files:** `MakeMkvService.cs`

```csharp
var result = await _runner.RunAsync(fileName, arguments, timeoutMs: 300_000, ct: ct);
// ...
using var reader = new StringReader(result.StdOut);
```

The entire MakeMKV info output (potentially hundreds of kilobytes) is buffered in a `string` before being parsed line-by-line. Use `RunStreamingAsync` instead to process lines as they arrive.

### 4.5 — SignalR progress broadcasts on every progress tick

The `MkvProgress` and `TranscodeProgress` callbacks call `BroadcastJobUpdate` on every progress percentage change (potentially 0–100). For fast rips, this floods SignalR. Consider throttling to at most 1 update per 200ms.

---

## 5. C# Best Practices

### 5.1 — `goto` statement in `ArmRipperService.cs`

Using `goto afterMakeMkv;` is a code smell in modern C#. The method should be decomposed so control flow is managed by method calls and early returns, not labels.

### 5.2 — Missing `CancellationToken` propagation in several places

- `CompletedController.ScanCompletedFilesAsync` does not accept `CancellationToken`.
- `ProbeFileAsync` uses `Process.WaitForExit()` instead of `WaitForExitAsync(CancellationToken)`.
- Several `db.SaveChangesAsync()` calls in the WebUi controllers omit the `CancellationToken`.

### 5.3 — `Job` entity uses `[NotMapped]` for transient fields

The `[NotMapped]` attributes on `MakeMkvProgress`, `TranscodeProgress`, and `ProgressMessage` are correct. However, the initial migration `20260610044322_Initial.cs` has columns for `MakeMkvProgress` and `TranscodeProgress` in the `jobs` table, which means those columns exist in the database but are never populated from EF. This is fine but the migration should have excluded them.

### 5.4 — `PasswordHasher<User>` usage — verify result correctly

**Files:** `AuthController.cs`

```csharp
var result = hasher.VerifyHashedPassword(user, user.PasswordHash, password);
if (result != PasswordVerificationResult.Success)
```

This is correct but should also handle `PasswordVerificationResult.SuccessRehashNeeded` — if the password hash format is upgraded by ASP.NET Core, the controller should rehash and save. Absent that, users will be locked out after a framework update.

### 5.5 — `GetLocalIpAddress` skips `172.x.x.x` addresses

**Files:** `NotificationService.cs`

```csharp
if (!ip.ToString().StartsWith("172."))
```

This is too aggressive. `172.x.x.x` includes both Docker bridge networks (`172.17.0.0/16` etc.) and legitimate non-public IPs. The method should check `IsPrivate()`, or better, use the actual network interface the app is bound to.

---

## 6. ARM‑Sharp / Ripping Pipeline Review

### 6.1 — Job lifecycle correctness

The `Conductor` orchestrates the pipeline: `Setup → Identify → Rip → Transcode → Finalize → Done`. Stage idempotency via `CompletedStages` is well-implemented. The `ManualWaitResume` pattern for user-interruptible waiting is clean.

### 6.2 — Watcher service safety

`BackgroundRipService` uses `ConcurrentDictionary` to track active rips and prevents duplicate rips on the same device. The `CancellationTokenSource.CreateLinkedTokenSource` correctly links external cancellation. **However**, the `_ = Task.Run(...)` discarding means exceptions from background rips are silently lost after the `try/catch` in the lambda — this is acceptable since the error is logged, but the cancellation should also be properly disposed if already cancelled.

### 6.3 — Queue orchestration

There is no persistent job queue. Each disc discovery directly triggers `StartRip`. If the system is already processing a disc and another is inserted, the `ConcurrentDictionary` prevents a duplicate rip on the same device path, but a different disc on a different device would also start immediately. For production workloads, a proper Channel/BufferBlock-backed queue with concurrency limits would be beneficial.

### 6.4 — External tool invocation

`CliProcessRunner` properly handles timeouts, cancellation, stdout/stderr redirection, and process tree killing. However:

- The `AppendToLogAsync` method is **never called** from anywhere (dead code).
- `RunStreamingAsync` has a race where stderr is fully read only after the process exits — stderr output is not streamed to the caller.

### 6.5 — Idempotency and error recovery

The `CompletedStages` pipe-delimited string is a pragmatic approach. However, if a stage partially fails (e.g., some tracks ripped but others failed), the stage is marked complete and will be skipped on retry. There is no "partial completion" tracking — the entire stage must succeed or the job is marked `Failure`.

### 6.6 — Concurrency model

- `CliProcessRunner` is **Singleton** and thread-safe (no instance state).
- `ArmDbContext` is **Scoped** — correct per EF Core convention.
- `BackgroundRipService` creates a new DI scope per job — correct.
- The `MaxConcurrentTranscodes` sleep-check loops (`Process.GetProcessesByName`) are polling-based and coarse (20s polling interval). This works but a semaphore-based approach would be more efficient.

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
