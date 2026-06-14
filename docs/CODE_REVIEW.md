# Arm-Sharp Comprehensive Code Review

**Date:** 2026-06-14
**Scope:** `ArmRipper.Core`, `ArmRipper.WebUi`, `ArmRipper.Cli`, `ArmRipper.Core.Tests`

---

## 1. Summary

Arm-Sharp is a C# (.NET 10) port of the automatic-ripping-machine — a Docker-based optical disc ripping pipeline. The system identifies disc types (DVD, Blu-ray, CD, data), rips via MakeMKV, transcodes via HandBrake or FFmpeg, and serves a real-time SignalR-backed web UI via ASP.NET Core MVC. The architecture is sound: a `Conductor` orchestrator dispatches to typed service interfaces (`IIdentifyService`, `IArmRipperService`, `IHandBrakeService`, etc.), with EF Core + SQLite for persistence and YAML config overlay for ARM compatibility.

The code is largely idiomatic C# with good use of DI, `IAsyncEnumerable`, and `IProgress<T>`. The test suite covers core logic and service behavior. However, several concurrency and resource-management issues need attention before production use.

---

## 2. Critical Issues

### 2.1 `lock(db) { db.SaveChanges(); }` in InlineProgress Callbacks — Thread-Safety & Deadlock Risk

**Location:** `ArmRipperService.cs`, `MkvProgress` and `TranscodeProgress` methods (~lines 490–510).

```csharp
private IProgress<int> MkvProgress(Job job, string message, CancellationToken ct) =>
    new InlineProgress<int>(pct =>
    {
        job.MakeMkvProgress = pct;
        job.ProgressMessage = message;
        try { lock (db) { db.SaveChanges(); } } catch (Exception ex) { ... }
        BroadcastJobUpdate(job);
    });
```

**Problem:** EF Core `DbContext` is **not thread-safe by design**. Wrapping `SaveChanges()` (a synchronous call — `SaveChangesAsync()` is the async variant) in a `lock` does not make it safe. For SQLite, concurrent writes cause `SQLITE_BUSY` exceptions. The `IProgress<T>.Report()` callback runs on an arbitrary thread (from `CliProcessRunner`'s output-parsing loop), which can race with the main pipeline thread also calling `SaveChangesAsync()`. Additionally, calling synchronous `SaveChanges()` on a scoped DbContext from a background thread while the owning scope may be disposed causes `ObjectDisposedException`.

**Fix:** Remove `lock(db) { db.SaveChanges(); }` entirely. Instead, queue progress updates to a `Channel<JobProgressUpdate>` and have a dedicated background loop flush them periodically (e.g., every 2 seconds) with proper `SaveChangesAsync()`. If immediate DB writes are required, use a **separate, short-lived scope** created via `IServiceScopeFactory` for each progress update.

### 2.2 Raw `new HttpClient()` in Per-Call Notification Methods — Socket Exhaustion

**Location:** `NotificationService.cs`, `SendRemoteNotificationsAsync` (~line 70) and `MakeMkvService.cs`, `FetchBetaKeyAsync` (~line 70).

```csharp
// NotificationService.cs
private async Task SendRemoteNotificationsAsync(...)
{
    using var client = new HttpClient();   // <-- new per call
    client.Timeout = TimeSpan.FromSeconds(10);
    ...
}

// MakeMkvService.cs
private static async Task<string?> FetchBetaKeyAsync(CancellationToken ct)
{
    using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };  // <-- new per call
    ...
}
```

**Problem:** Creating and disposing `HttpClient` per request causes socket exhaustion under load — each instance creates a new underlying connection that lingers in `TIME_WAIT`. On Linux, this manifests as ephemeral port exhaustion after sustained use.

**Fix:** Both cases should use `IHttpClientFactory` — `NotificationService` should accept an `IHttpClientFactory` in its constructor and call `_httpClientFactory.CreateClient()`. `MakeMkvService` already has DI; inject `IHttpClientFactory` there as well. The `FetchBetaKeyAsync` method is `static` — make it an instance method or inject a factory.

### 2.3 `CliProcessRunner.RunStreamingAsync` Leaks Process Handles

**Location:** `CliProcessRunner.cs`, `RunStreamingAsync` method (line ~100+).

```csharp
public async IAsyncEnumerable<string> RunStreamingAsync(...)
{
    var process = new Process { StartInfo = ... };
    process.Start();
    // ... reads lines ...
    // process is never disposed or killed if enumeration stops early
}
```

**Problem:** The `Process` object is created but **never disposed**. When the consumer breaks out of the `await foreach` loop early (cancellation, error), the process continues running as a zombie. There is no `finally` block, no `using` declaration, and no `CancellationToken` registration to kill the process.

**Fix:** Wrap the process lifecycle in a `try/finally` that kills and disposes the process. Register the `CancellationToken` to kill the process on cancellation. Example pattern:

```csharp
public async IAsyncEnumerable<string> RunStreamingAsync(...)
{
    using var process = new Process { StartInfo = ... };
    process.Start();
    using var ctReg = ct.Register(() => { try { process.Kill(true); } catch { } });
    try
    {
        // ... enumerate lines ...
    }
    finally
    {
        if (!process.HasExited)
        {
            try { process.Kill(true); } catch { }
        }
    }
}
```

### 2.4 Stale Navigation Property in `Conductor.ProcessJobAsync`

**Location:** `Conductor.cs`, `ProcessJobAsync` (~line 185).

```csharp
var cfg = job.Config ?? db.ConfigSnapshots.FirstOrDefault(c => c.JobId == job.Id);
```

**Problem:** `job.Config` was eagerly set when `ConfigSnapshot` was created in `SetupJobAsync`, but by the time `ProcessJobAsync` runs, the Job entity has been reloaded via `db.Entry(job).ReloadAsync(ct)`, which does **not** reload navigation properties. If the Job was reloaded without `.Include(j => j.Config)`, `job.Config` could be null even though the DB row exists, causing an unnecessary second query — which may return a **detached** entity that then gets re-attached unexpectedly during later `SaveChangesAsync` calls.

**Fix:** Always use eager loading when reloading: replace `db.Entry(job).ReloadAsync(ct)` with:
```csharp
await db.Entry(job).Reference(j => j.Config).LoadAsync(ct);
await db.Entry(job).ReloadAsync(ct);
```
Or, more simply, don't reload Job at all for config reads — the snapshot is immutable after creation.

### 2.5 SQLite File Lock Contention Under Concurrent Access

**Location:** Multiple services (`MakeMkvService`, `HandBrakeService`, `FfmpegService`, `ArmRipperService`) all call `db.SaveChangesAsync(ct)` independently within the same scoped operation.

**Problem:** SQLite allows only one writer at a time. During a rip, multiple components (MakeMKV progress callback, HandBrake progress callback, the main Conductor loop, and Web UI SignalR broadcast callbacks) all write to the same `arm-sharp.db`. When `SaveChangesAsync` fails with `SQLITE_BUSY`, the current `catch { /* best effort */ }` silently swallows the error. This means progress updates can be silently lost, and the UI can show stale data for long periods.

**Fix:**
1. Configure the SQLite connection string with a busy timeout: `Data Source=...;Busy Timeout=5000;`
2. Implement a retry policy for `SaveChangesAsync` with exponential backoff (3 retries, 100ms–400ms).
3. Batch writes: accumulate progress updates and flush every 2–5 seconds rather than on every percentage point.

### 2.6 `JobDupeCheckAsync` Eager-Loads Full Entity Graphs

**Location:** `Conductor.cs`, `JobDupeCheckAsync` (~line 430).

```csharp
var previousRips = await db.Jobs
    .Where(j => j.Label == job.Label && j.Status == JobState.Success)
    .ToListAsync(ct);
```

**Problem:** This loads **all columns** of all matching `Job` rows, including `Tracks` and `Config` navigation properties (if lazy loading is enabled). For a long-running system with many completed rips, this loads hundreds of megabytes of unnecessary data.

**Fix:** Use a projection query:
```csharp
var previousRips = await db.Jobs
    .Where(j => j.Label == job.Label && j.Status == JobState.Success)
    .Select(j => new { j.Title, j.Year, j.HasNiceTitle, j.VideoType, j.PosterUrl })
    .ToListAsync(ct);
```

---

## 3. Structural & Maintainability Issues

### 3.1 `RipVisualMediaAsync` Is Too Long (~400 Lines) — Violates Single Responsibility

The method mixes:
- Path computation
- Duplicate checking
- MakeMKV rip dispatch (with multiple branching strategies)
- File-size monitoring
- Transcode dispatch
- File move logic
- Emby notification
- Cleanup

**Recommendation:** Extract sub-methods into a pipeline class: `MakeMkvStage`, `TranscodeStage`, `FinalizeStage`. Each stage accepts a `RipContext` (Job + paths) and returns a result.

### 3.2 `MakeMkvService` Has No Interface — Testing Requires Concrete Type

`MakeMkvService` is registered directly as `builder.Services.AddScoped<MakeMkvService>()` and consumed as the concrete type. All other services use interfaces (`IHandBrakeService`, `IFfmpegService`, etc.). This inconsistency means `ArmRipperService` and `Conductor` are tightly coupled to the MakeMKV implementation.

**Recommendation:** Define `IMakeMkvService` with the public surface (`GetTrackInfoAsync`, `GetTrackInfoWithCacheAsync`, `RipTrackAsync`, `RipAllTitlesAsync`, `EnsureKeyAsync`) and register via the interface.

### 3.3 `ConfigSnapshot` Duplicates `ArmSettings` Field-for-Field

`ConfigSnapshot` has ~50 properties that are an exact copy of `ArmSettings`. Any new setting must be added in both places plus the mapping code in `SetupJobAsync`.

**Recommendation:** Replace `ConfigSnapshot` with a JSON column storing `ArmSettings` serialized as a blob. Or use EF Core owned types / value conversion to store the entire config object as JSON:

```csharp
modelBuilder.Entity<Job>()
    .OwnsOne(j => j.ConfigSnapshot, cfg => cfg.ToJson("ConfigJson"));
```

### 3.4 `ArmYamlConfigLoader` Uses Regex, Not a YAML Parser

The hand-rolled regex parser handles only flat key-value YAML. It silently ignores:
- Nested structures
- Multi-line values
- YAML anchors/aliases
- Boolean/numeric type coercion

**Recommendation:** Use `YamlDotNet` (already available via NuGet) for proper YAML deserialization. ARM's config file is flat-key, so the regex approach works, but it's fragile.

### 3.5 Mixed `SaveChanges` vs `SaveChangesAsync` Patterns

Some progress callbacks call `db.SaveChanges()` (sync), others call `db.SaveChangesAsync(ct)`. The synchronous calls block threads in the thread pool and can cause deadlocks in ASP.NET contexts.

**Recommendation:** Standardize on `SaveChangesAsync(ct)` everywhere, and use a dedicated producer-consumer queue for progress writes.

### 3.6 Error-Handling Strategy Is Inconsistent

- `Conductor.RunAsync` catches `Exception` and marks the job as `Failure` — good.
- `ArmRipperService.RipVisualMediaAsync` throws after catching `Exception mkvError` with `throw;` — which is caught by the outer `Conductor` catch, which is correct but the re-throw loses context from partial work.
- `HandBrakeService.TranscodeMkvAsync` continues to next file on failure (good) but `TranscodeMainFeatureAsync` throws on failure (inconsistent).
- `FfmpegService.TranscodeMkvAsync` throws on first file failure, skipping remaining files.

**Recommendation:** Adopt a consistent strategy: per-track operations should **never throw** on individual track failure — log, mark track as failed, continue. Only infrastructure failures (disk full, process crash) should propagate.

---

## 4. Performance Review

### 4.1 `CliProcessRunner.ReadAllLinesAsync` Uses `List<string>` Accumulation

```csharp
private static async Task<List<string>> ReadAllLinesAsync(StreamReader reader, CancellationToken ct)
{
    var lines = new List<string>();
    while (await reader.ReadLineAsync(ct) is { } line)
        lines.Add(line);
    return lines;
}
```

Then joined with `string.Join("\n", lines)`. For MakeMKV info output (can be thousands of lines), this is wasteful.

**Fix:** Use `StringBuilder` or read directly into a single string:
```csharp
var text = await reader.ReadToEndAsync(ct);
return text.Split('\n').ToList();  // or just return the string
```

### 4.2 `NotificationService.NotifyEntryAsync` Blocks on Synchronous DNS

```csharp
private static string GetLocalIpAddress()
{
    var host = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName());  // sync!
    ...
}
```

**Problem:** `Dns.GetHostEntry` is a synchronous blocking call that can take seconds, holding a thread-pool thread. Called during the `NotifyEntryAsync` path.

**Fix:** Use `await Dns.GetHostEntryAsync(...)`.

### 4.3 Excessive `SaveChangesAsync` Calls — Chunked Writes Unnecessary

In `ArmRipperService.RipVisualMediaAsync`, `SaveChangesAsync` is called after every individual property mutation (`job.Stage = ...`, `job.ProgressMessage = ...`). During a 30-minute rip with per-second progress updates, this results in ~1,800 individual DB writes.

**Fix:** Batch writes. Use a timer-based flush: accumulate changes and write every 5 seconds or when a stage transition occurs.

### 4.4 `string.Replace` in Bash Command Escaping Is Brittle

```csharp
// Conductor.cs RipDataAsync
await runner.RunAsync("bash", $"-c \"{cmd.Replace("\"", "\\\"")}\"", ...);

// Conductor.cs RipMusicAsync
await runner.RunAsync("bash", $"-c \"{cmd.Replace("\"", "\\\"")}\"", ...);
```

**Problem:** Double-escaping issues — the outer `$""` interpolation, plus inner `Replace("\"", "\\\"")`, creates confusing escape chains. Also allocates a new string per call.

**Fix:** Pass the command as stdin to bash: `bash -s <<< "..."` or use a temp script file. Better: call `dd` and `abcde` directly via `CliProcessRunner` instead of wrapping in bash.

### 4.5 LINQ `ToList()` Before Enumeration in Hot Paths

```csharp
// HandBrakeService.cs
var mkvFiles = Directory.EnumerateFiles(rawPath, "*.mkv").ToList();
// FfmpegService.cs
var mkvFiles = Directory.EnumerateFiles(rawPath, "*.mkv").ToList();
```

The `ToList()` forces immediate enumeration of all files. If there are hundreds of tracks on a disc, this allocates unnecessarily.

**Fix:** If you need the count for the progress message, count first, then enumerate:

```csharp
var mkvFiles = Directory.EnumerateFiles(rawPath, "*.mkv");
var fileList = mkvFiles as IReadOnlyCollection<string> ?? mkvFiles.ToList();
```

Or use two passes: one for count, one for processing.

---

## 5. C# Best Practices

### 5.1 Missing `ConfigureAwait(false)` in Core Library

`ArmRipper.Core` is a class library consumed by both `ArmRipper.WebUi` (ASP.NET Core) and `ArmRipper.Cli` (console). All `await` calls in the Core project should use `ConfigureAwait(false)` to avoid capturing the synchronization context:

```csharp
await db.SaveChangesAsync(ct).ConfigureAwait(false);
```

This prevents potential deadlocks if the library is ever consumed in a sync-over-async pattern and improves performance by avoiding context marshaling.

### 5.2 `JobLogger` Does Not Properly Implement `IDisposable`

```csharp
public void Dispose() => _fileWriter.Dispose();
```

The `JobLogger` holds a `StreamWriter` but does not implement the full disposal pattern. If `Dispose()` is never called (it's created per-job scope but the scope disposal may not trigger it), the file handle leaks.

**Fix:** Implement `IAsyncDisposable` and flush asynchronously:

```csharp
public async ValueTask DisposeAsync()
{
    await _fileWriter.FlushAsync();
    await _fileWriter.DisposeAsync();
}
```

### 5.3 `Job` Entity Uses `init` Accessor but Is Modified After Creation

```csharp
public class Job
{
    public int Id { get; init; }           // good — DB-generated
    public ICollection<Track> Tracks { get; init; } = new List<Track>();  // good
}
```

However, other properties use `{ get; set; }` which allows arbitrary mutation. Consider making properties that should only change through specific service methods use `{ get; private set; }` or at least mark them with comments indicating which service owns mutation.

### 5.4 `NotificationService` Sends HTTP Requests Without Response Validation

```csharp
await client.PostAsync("https://api.pushbullet.com/v2/pushes", content, ct);
```

The response is never checked for success/failure status codes. If Pushbullet returns 401 (invalid key) or 429 (rate limited), the error is silently swallowed.

**Fix:** Check `response.EnsureSuccessStatusCode()` and log HTTP failures at warning level.

### 5.5 Source-Generated Regex Attribute Usage (Good Practice)

```csharp
[GeneratedRegex(@"^([A-Z][A-Z_0-9]+)\s*:\s*(.*)")]
private static partial Regex YamlLineRegexFactory();
```

This is correct and performant. The same pattern should be applied to other regexes in `MakeMkvService`, `IdentifyService`, and `FfmpegService` (which currently use `new Regex(...)` at runtime).

### 5.6 Deprecated `System.IO.File` While Having `using System.IO`

`Job.cs` has `using System.IO;` at the top but inside `GetLogFilePath` uses `System.IO.Path.Combine(...)`. While `System.IO` is imported, the explicit qualification is inconsistent with the rest of the codebase.

---

## 7. Improved Snippets

### 7.1 Fix `lock(db)` Progress Callbacks — Use Scoped DB Context

Replace the current progress handlers in `ArmRipperService.cs`:

```csharp
// BEFORE (problematic)
private IProgress<int> MkvProgress(Job job, string message, CancellationToken ct) =>
    new InlineProgress<int>(pct =>
    {
        job.MakeMkvProgress = pct;
        job.ProgressMessage = message;
        try { lock (db) { db.SaveChanges(); } } catch (Exception ex) { ... }
        BroadcastJobUpdate(job);
    });

// AFTER (safe)
private record ProgressUpdate(int JobId, int? MkvProgress, int? TranscodeProgress, string? Message);

private readonly Channel<ProgressUpdate> _progressChannel =
    Channel.CreateBounded<ProgressUpdate>(new BoundedChannelOptions(256)
    {
        FullMode = BoundedChannelFullMode.DropOldest
    });

private IProgress<int> MkvProgress(Job job, string message, CancellationToken ct)
{
    return new InlineProgress<int>(pct =>
    {
        job.MakeMkvProgress = pct;
        job.ProgressMessage = message;
        _progressChannel.Writer.TryWrite(new ProgressUpdate(
            job.Id, pct, null, message));
        BroadcastJobUpdate(job);
    });
}

// Background flush loop (started in Conductor or a hosted service):
private async Task FlushProgressLoopAsync(CancellationToken ct)
{
    await foreach (var update in _progressChannel.Reader.ReadAllAsync(ct))
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ArmDbContext>();
            var job = await db.Jobs.FindAsync(new object[] { update.JobId }, ct);
            if (job is not null)
            {
                if (update.MkvProgress.HasValue)
                    job.MakeMkvProgress = update.MkvProgress;
                if (update.TranscodeProgress.HasValue)
                    job.TranscodeProgress = update.TranscodeProgress;
                if (update.Message is not null)
                    job.ProgressMessage = update.Message;
                await db.SaveChangesAsync(ct);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to flush progress update");
        }
    }
}
```

### 7.2 Fix `new HttpClient()` — Use `IHttpClientFactory`

```csharp
// NotificationService.cs — BEFORE
private async Task SendRemoteNotificationsAsync(ConfigSnapshot? cfg, ...)
{
    using var client = new HttpClient();
    client.Timeout = TimeSpan.FromSeconds(10);
    ...
}

// NotificationService.cs — AFTER
public sealed class NotificationService(
    ILogger<NotificationService> logger,
    ArmDbContext db,
    ICliProcessRunner runner,
    IHttpClientFactory httpClientFactory,
    IEnumerable<INotificationBroadcaster> broadcasters)
{
    private async Task SendRemoteNotificationsAsync(ConfigSnapshot? cfg, ...)
    {
        using var client = httpClientFactory.CreateClient("Notifications");
        ...
    }
}

// Registration in Program.cs:
builder.Services.AddHttpClient("Notifications", client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("arm/1.0");
});
```

### 7.3 Fix `Process` Leak in `RunStreamingAsync`

```csharp
// BEFORE (leaks on early enumeration stop)
public async IAsyncEnumerable<string> RunStreamingAsync(...)
{
    var process = new Process { StartInfo = ... };
    process.Start();
    // ... never disposed ...
}

// AFTER (proper cleanup)
public async IAsyncEnumerable<string> RunStreamingAsync(
    string fileName, string arguments, string? workingDirectory = null,
    [EnumeratorCancellation] CancellationToken ct = default)
{
    using var process = new Process
    {
        StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        }
    };

    process.Start();
    using var ctReg = ct.Register(() =>
    {
        try { process.Kill(entireProcessTree: true); } catch { }
    });

    try
    {
        using var reader = process.StandardOutput;
        while (await reader.ReadLineAsync(ct) is { } line)
            yield return line;
    }
    finally
    {
        if (!process.HasExited)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
        }
    }
}
```

### 7.4 Fix JobDupeCheckAsync Projection

```csharp
// BEFORE (loads full entity graph)
var previousRips = await db.Jobs
    .Where(j => j.Label == job.Label && j.Status == JobState.Success)
    .ToListAsync(ct);

// AFTER (projection only)
var previousRips = await db.Jobs
    .Where(j => j.Label == job.Label && j.Status == JobState.Success)
    .OrderByDescending(j => j.StopTime)
    .Select(j => new
    {
        j.Title, j.Year, j.HasNiceTitle, j.VideoType, j.PosterUrl
    })
    .Take(10)
    .ToListAsync(ct);

if (previousRips.Count == 1)
{
    var prev = previousRips[0];
    job.Title = job.TitleAuto = prev.Title ?? job.Label;
    job.Year = job.YearAuto = prev.Year;
    job.HasNiceTitle = prev.HasNiceTitle;
    job.VideoType = job.VideoTypeAuto = prev.VideoType;
    job.PosterUrl = job.PosterUrlAuto = prev.PosterUrl;
    await db.SaveChangesAsync(ct);
    return true;
}
```

### 7.5 Add Busy Timeout to SQLite Connection

```csharp
// Program.cs & ArmRipper.Cli/Program.cs — BEFORE
var connectionString = ...;
builder.Services.AddDbContext<ArmDbContext>(options =>
    options.UseSqlite(connectionString));

// AFTER
var connectionString = ... + ";Busy Timeout=5000;Pooling=True;";
builder.Services.AddDbContext<ArmDbContext>(options =>
    options.UseSqlite(connectionString));
```

---

## 8. Final Recommendations

**Highest-impact changes to make first (ordered by risk):**

| Priority | Issue | Risk | Effort |
|----------|-------|------|--------|
| **P0** | Remove `lock(db)` in progress callbacks — use Channel-based flush | Data corruption, crashes | Medium |
| **P0** | Fix `RunStreamingAsync` process leak — add try/finally with kill | Zombie processes, resource exhaustion | Small |
| **P1** | Replace `new HttpClient()` with `IHttpClientFactory` in NotificationService and MakeMkvService | Socket exhaustion under load | Small |
| **P1** | Add SQLite busy timeout + retry policy for SaveChangesAsync | Silent data loss on progress writes | Small |
| **P1** | Remove throw-on-first-failure in FfmpegService.TranscodeMkvAsync — continue to next file | Single track failure abandons remaining tracks | Small |
| **P2** | Add `ConfigureAwait(false)` throughout Core library | Potential deadlocks, performance | Medium (mechanical) |
| **P2** | Extract `IMakeMkvService` interface for testability | Test coupling | Medium |
| **P2** | Projection query in `JobDupeCheckAsync` | Memory/performance with large DB | Small |
| **P3** | Break up `RipVisualMediaAsync` and `Conductor.ProcessJobAsync` into smaller methods | Maintainability | Large |
| **P3** | Replace YAML regex loader with YamlDotNet | Correctness for edge cases | Medium |
| **P3** | Standardize error-handling: per-track failures never throw | Resilience | Medium |

**Overall assessment:** The codebase is well-structured and largely follows modern C# conventions. The critical issues are all in the concurrency/reliability domain — fixing the DB threading problem and process lifecycle management will bring the system to production readiness. The architectural decisions (DI, EF Core, SignalR, ASP.NET Core MVC) are sound.
