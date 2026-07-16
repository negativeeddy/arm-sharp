# 🔍 Comprehensive Code Review — ARM Sharp (`arm-sharp`)

**Date:** 2026-07-15  
**Scope:** `ArmRipper.Core`, `ArmRipper.WebUi`, `ArmRipper.Cli`, `ArmMedia.Core`, provider projects, and tests.  
**Overall Grade: B+** — solid architecture, good test coverage for core paths, good use of DI/EF Core/SignalR.

---

## Stage 1 — Critical Issues (Must-Fix)

### 🔴 1.1 — `CliProcessRunner.RunStreamingAsync` throws on non-zero exit

**File:** `src/ArmRipper.Core/Infrastructure/CliProcessRunner.cs`  
**Lines:** ~140-145

```csharp
if (process.ExitCode != 0)
{
    throw new InvalidOperationException(
        $"Process '{fileName}' exited with code {process.ExitCode}. " +
        $"Arguments: {arguments}");
}
```

**Problem:** All MakeMKV commands exit with codes that convey rich status (e.g., 253 = no disc, 254 = key invalid, 0 = success with some failures possible). Throwing `InvalidOperationException` discards this information and prevents callers from distinguishing between "disc not found" and "real failure." The `RunAsync` method does NOT throw on non-zero exit — it returns the exit code in `CliResult`. This creates an inconsistent API.

**Fix:** Remove the throw from `RunStreamingAsync`. Instead, yield the exit code as a final sentinel or expose it via an `out` parameter. Callers should inspect the exit code themselves.

---

### 🔴 1.2 — `async void` via `Task.Run` in `BackgroundRipService.StartRip`

**File:** `src/ArmRipper.Core/Infrastructure/BackgroundRipService.cs`  
**Lines:** ~80

```csharp
_ = Task.Run(async () =>
{
    // ... rip logic ...
}, cts.Token);
```

**Problem:** The fire-and-forget `_ = Task.Run(...)` is effectively `async void` — exceptions thrown inside are only logged, not surfaced to callers. If the scope factory fails or the rip crashes in an unexpected way, `StartRipResult.Accepted` was already returned. The caller has no way to know. This is especially dangerous because `StartRip` returns before the sysfs check that happens inside the `Task.Run`.

**Fix:** Move the sysfs check (`IsMediaPresent`) **before** returning `Accepted`. Consider returning `Task<StartRipResult>` or using a channel-based approach where the caller can observe failures.

---

### 🔴 1.3 — `.GetAwaiter().GetResult()` deadlock risk in `StartImportJob`

**File:** `src/ArmRipper.Core/Infrastructure/BackgroundRipService.cs`  
**Lines:** ~185

```csharp
var job = conductor.CreateImportJobAsync(rawFilePath, title, year, videoType, discType, ct)
    .GetAwaiter().GetResult();
```

**Problem:** Blocking on async code with `.GetAwaiter().GetResult()` in a method that may be called from an ASP.NET request context can cause deadlocks if `ConfigureAwait(false)` isn't used consistently in the downstream call chain. Even outside ASP.NET, this is a code smell.

**Fix:** Make `StartImportJob` async and return `Task<int>`, or restructure to avoid sync-over-async entirely.

---

### 🔴 1.4 — Same sync-over-async in `Program.cs` for settings seeding

**File:** `src/ArmRipper.WebUi/Program.cs`  
**Lines:** ~195

```csharp
SettingsHelper.SeedFromFileAsync(db, seedSettings, reset).GetAwaiter().GetResult();
```

**Problem:** Same deadlock risk. Called during app startup, which is less risky but still a material code smell. If `SeedFromFileAsync` ever does I/O on the synchronization context, this will deadlock.

**Fix:** Use `.GetAwaiter().GetResult()` only if `SeedFromFileAsync` is confirmed to never capture the context. Better: restructure startup to async — ASP.NET 10 supports async startup via `IHostedService` or `WebApplication` lifecycle hooks.

---

### 🔴 1.5 — Job state mutations without concurrency protection

**File:** `src/ArmRipper.Core/Rip/ArmRipperService.cs`  
**Lines:** ~65

```csharp
job.TransitionToStage(RipStage.Identify);
job.ProgressMessage ??= "Preparing to rip...";
await db.SaveChangesAsync(ct);
```

**Problem:** `TransitionToStage` sets `Stage` and `StageStartTime` but does not validate the current stage before transitioning. If two code paths both try to transition (e.g., a resume while a rip is in progress), the `CompletedStages` list and `Stage` can become inconsistent. There's no optimistic concurrency token on the Job entity, so concurrent writes silently clobber each other.

**Fix:** Add a `[Timestamp]` or `[ConcurrencyCheck]` row version to the `Job` entity. Use EF Core's concurrency conflict handling (`DbUpdateConcurrencyException`) in the conductor loop.

---

### 🔴 1.6 — `Resume` endpoint: synchronous DB save, no cancellation token

**File:** `src/ArmRipper.WebUi/Controllers/JobsController.cs`  
**Method:** `Resume`

```csharp
job.Status = JobState.Active;
job.StopTime = null;
job.Errors = null;
job.ProgressMessage = "Resuming from checkpoint...";
db.SaveChanges();  // SYNCHRONOUS — no async, no ct
```

**Problem:** `db.SaveChanges()` is used synchronously in an async controller method. This blocks the ASP.NET thread pool thread. Also, no cancellation token is passed.

**Fix:** Use `await db.SaveChangesAsync()` and pass a `CancellationToken`.

---

## Stage 2 — Structural & Maintainability Issues

### 🟡 2.1 — `ArmRipperService` is too large (600+ lines)

**File:** `src/ArmRipper.Core/Rip/ArmRipperService.cs`

The class handles: MakeMKV ripping, track selection, widescreen preference logic, DiscDb track mapping, transcode dispatch, file matching, file moves, Emby refresh, permissions, cleanup, and notifications. It violates the Single Responsibility Principle.

**Fix:** Extract:
- `TrackSelectionService` — widescreen preference, main-feature selection, eligibility
- `FileMatchService` — matching ripped files to DB tracks
- `CleanupService` — deleting raw files, directory removal

---

### 🟡 2.2 — ConfigSnapshot duplication across `RunForkedTranscodeAsync` and `CreateImportJobAsync`

**File:** `src/ArmRipper.Core/Rip/Conductor.cs`  
**Lines:** Two places, ~150 lines each

The same 40+ property mapping block (`sourceConfig?.Xxx ?? armSettings.Xxx`) appears verbatim in both methods. This is a maintenance hazard — adding a new config field requires updating 2-3 locations.

**Fix:** Create a `ConfigSnapshot.FromSettings(ArmSettings, ConfigSnapshot? source)` factory method.

---

### 🟡 2.3 — `MakeMkvService.GetTrackInfoAsync` is 250+ lines with manual parser state

**File:** `src/ArmRipper.Core/Rip/MakeMkvService.cs`

The method manually tracks `currentTid`, `seconds`, `aspect`, `fps`, `filename`, etc. as mutable locals and flushes via `FinalizeTrack` with `ref` parameters. This is very hard to reason about and test in isolation.

**Fix:** The existing `MakeMkvOutputParser` already exists — lean on it more. Consider a state machine or a dedicated `MakeMkvInfoParser` class that yields completed `Track` objects.

---

### 🟡 2.4 — Stringly-typed `VideoType` and `DiscType`

```csharp
job.VideoType = "series";  // magic string
if (job.VideoType == "series" || job.VideoType == "tv") { ... }
```

**Problem:** `VideoType` and `DiscType` comparisons are scattered through the codebase as string literals. `DiscType` has an enum but `VideoType` doesn't.

**Fix:** Add a `VideoType` enum and use it throughout. Add extension methods like `IsTvSeries()` to eliminate magic string comparisons.

---

### 🟡 2.5 — Inconsistent service lifetime annotations

The architecture doc states some services are `Scoped` but in the code some are actually registered as `Scoped` while others that reference per-job state are transient. The `NotificationService` has no interface and is resolved directly.

**Fix:** Add `INotificationService` interface. Audit all lifetimes against their actual dependencies.

---

### 🟡 2.6 — `DvdCrc64` in the Rip namespace

**File:** `src/ArmRipper.Core/Rip/DvdCrc64.cs`

A CRC64 computation utility lives in `ArmRipper.Core/Rip/` — it's not a rip operation, it's an identification/disc-fingerprinting utility. Misplaced.

**Fix:** Move to `ArmRipper.Core/Infrastructure/` or a new `Utilities/` folder.

---

## Stage 3 — Performance Review

### 🟠 3.1 — `ConfigSnapshot` has ~50 properties, each stored as a column

Each config snapshot is a row with 50+ columns. For most jobs, most fields are defaults. This causes:
- Wide rows in SQLite (poor cache locality)
- Large memory footprint per in-progress job
- Slow serialization for API responses that include config

**Fix:** Store only overridden values as a JSON blob (`ConfigOverrides`) and resolve defaults from `ArmSettings` at read time. This also eliminates the duplication problem from §2.2.

---

### 🟠 3.2 — `CheckForDupeFolder` likely does I/O in a loop

If called multiple times (it appears to be called twice in sequence for `transcodeOutPath` and `finalDirectory`), it may do redundant filesystem checks.

**Fix:** Cache `Directory.Exists` results within the job scope, or combine the two calls.

---

### 🟠 3.3 — `CompletedStages` as pipe-delimited string (O(n) lookup)

**File:** `src/ArmRipper.Core/Models/Job.cs`

```csharp
public bool IsStageComplete(RipStage stage)
{
    return CompletedStages.Split('|', StringSplitOptions.RemoveEmptyEntries)
        .Any(s => s.Equals(name, StringComparison.OrdinalIgnoreCase));
}
```

Every call allocates a string array. Called multiple times per stage transition.

**Fix:** Parse once into a `HashSet<string>` at job load time, or use a bitmask enum (`[Flags]`) for stage tracking.

---

### 🟠 3.4 — `track.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase)` in a loop over files × tracks

**File:** `src/ArmRipper.Core/Rip/ArmRipperService.cs`

```csharp
foreach (var file in Directory.EnumerateFiles(makeMkvOutPath, "*.mkv"))
{
    var track = dbTracks.FirstOrDefault(t => /* filename match */)
        ?? dbTracks.FirstOrDefault(t => /* track number match */);
}
```

This is O(files × tracks) with two `FirstOrDefault` scans per file. For large TV series discs with 20+ tracks, this matters.

**Fix:** Build a `Dictionary<string, Track>` keyed by filename (case-insensitive) and by track number, then do O(1) lookups.

---

## Stage 4 — C# / .NET 10 Best Practices

### ✅ Well Done
- File-scoped namespaces used throughout
- Primary constructors used on most service classes
- `IAsyncEnumerable` for streaming MakeMKV output — excellent pattern
- `System.Threading.Channels` for `RunStreamingAllAsync` — correct choice for producer/consumer
- `CancellationToken` propagation is mostly consistent
- Structured logging with `ILoggerFactory.CreateLogger("Category")` — good for terse categories
- `[EnumeratorCancellation]` on async enumerators

### ⚠️ Issues

#### 4.1 — `HttpClient` in `using` blocks despite `IHttpClientFactory` usage

**File:** `src/ArmRipper.Core/Notifications/NotificationService.cs`

```csharp
using var client = httpClientFactory.CreateClient("Notifications");
```

The factory already manages lifetimes. The `using` is harmless but misleading.

---

#### 4.2 — `Process.Start()` without async wait in `RunStreamingAsync`

**File:** `src/ArmRipper.Core/Infrastructure/CliProcessRunner.cs`

The `ReadLineAsync` loop reads stdout first, THEN stderr. If stderr fills its buffer before stdout is fully consumed, the process deadlocks. This is handled correctly in `RunStreamingAllAsync` via a separate `Task.Run` feeding a channel, but `RunStreamingAsync` has the same pattern without the channel — stdout is read, then stderr is drained after. If stderr fills up during the stdout read, the process hangs.

**Fix:** In `RunStreamingAsync`, drain stderr concurrently (as `RunAsync` does correctly with `Task.WhenAll`).

---

#### 4.3 — `ILoggerFactory` should be `ILogger<T>` where possible (✅ FIXED — switched to `nameof()`)

**File:** Various — 14 files across `src/ArmRipper.Core/`

**Previous pattern:**
```csharp
private readonly ILogger logger = loggerFactory.CreateLogger("Conductor");
```

**Fixed:** All 14 magic-string `CreateLogger("ClassName")` calls replaced with `nameof(ClassName)`. This keeps short category names (not the noisy full-namespace `ILogger<T>` names) while being refactor-safe. `CliProcessRunner` already used this pattern.

```csharp
private readonly ILogger logger = loggerFactory.CreateLogger(nameof(Conductor));
```

---

#### 4.4 — `readonly` missing on several fields

`_runner`, `_logger`, `_settings`, `_db`, `_httpClientFactory` in `MakeMkvService` are assigned only in the constructor but aren't `readonly`.

---

## Stage 5 — ARM-Sharp / Ripping Pipeline Review

### ✅ Well Done
- **Stage-based idempotency** via `CompletedStages` — allows resume after crash/shutdown
- **ConfigSnapshot** captures settings at rip time, decoupled from live changes
- **Eject cooldown** prevents false re-trigger after rip completion
- **DiscDb integration** for TV episode mapping is well-isolated
- **Forked transcode** and **import job** — excellent UX features
- **MakeMKV beta key auto-fetch** with primary + fallback sources
- **0-track fallback** for encrypted Blu-rays (rip all titles when info returns nothing)
- **PreferWidescreen** logic with configurable threshold

### ⚠️ Issues

#### 5.1 — `RunForkedTranscodeAsync` copies all 50 config fields manually

See §2.2. This is the most fragile code in the project — adding a single config field silently breaks forked/imported jobs.

---

#### 5.2 — Episode identification runs after rip but before transcode

**File:** `src/ArmRipper.Core/Rip/ArmRipperService.cs`

The `RunEpisodeIdentificationAsync` call happens between rip and transcode. If identification fails, the rip is already done — the user gets a ripped but un-transcoded disc.

**Fix:** Consider running identification before the rip stage, or making identification failure non-fatal.

---

#### 5.3 — `Job` entity mixes persisted and transient state

`MakeMkvProgress`, `TranscodeProgress`, `ProgressMessage` are `[NotMapped]` transient fields on the same EF entity. This is a pragmatic pattern but risks confusion — someone will eventually try to query on a `[NotMapped]` property.

**Fix:** Consider a separate `JobProgress` DTO for SignalR broadcasts, keeping the EF entity clean.

---

#### 5.4 — No MakeMKV output file validation before starting transcode

After rip, the code checks if `dbTracks.Any(t => t.Ripped)` but doesn't validate that the files on disk are non-empty MKV files. A zero-byte MKV from a failed rip would slip through.

---

#### 5.5 — `maxLength > 99998` magic number

**File:** `src/ArmRipper.Core/Rip/ArmRipperService.cs`

```csharp
else if (maxLength > 99998 && eligibleTracks.All(t => string.IsNullOrEmpty(t.EpisodeTitle)))
```

99998 is effectively "unlimited." Replace with a named constant or use `int.MaxValue` check.

---

## Stage 6 — Improved Snippets

### 6.1 — `CompletedStages` as a bitmask enum

```csharp
[Flags]
public enum RipStageFlags : long
{
    None      = 0,
    Setup     = 1 << 0,
    Identify  = 1 << 1,
    Rip       = 1 << 2,
    Transcode = 1 << 3,
    Finalize  = 1 << 4,
    Done      = 1 << 5,
}

// Usage:
public bool IsStageComplete(RipStage stage) =>
    (CompletedStagesFlags & (RipStageFlags)(1 << (int)stage)) != 0;

public void MarkStageComplete(RipStage stage) =>
    CompletedStagesFlags |= (RipStageFlags)(1 << (int)stage);
```

Eliminates string splitting/allocations entirely.

---

### 6.2 — ConfigSnapshot factory method

```csharp
public static ConfigSnapshot FromSettings(ArmSettings arm, ConfigSnapshot? source)
{
    return new ConfigSnapshot
    {
        SkipTranscode = source?.SkipTranscode ?? arm.SkipTranscode,
        MainFeature = source?.MainFeature ?? arm.MainFeature,
        // ... all other properties ...
    };
}
```

Replace the 50-line blocks in `RunForkedTranscodeAsync`, `CreateImportJobAsync`, and `SetupJobAsync`.

---

### 6.3 — Concurrent stderr drain in `RunStreamingAsync`

```csharp
public async IAsyncEnumerable<string> RunStreamingAsync(...)
{
    // ... process start ...

    // Drain stderr concurrently to avoid buffer-deadlock
    var stderrTask = Task.Run(async () =>
    {
        var lines = new List<string>();
        while (await process.StandardError.ReadLineAsync(CancellationToken.None) is { } line)
            lines.Add(line);
        return lines;
    });

    while (await process.StandardOutput.ReadLineAsync(ct) is { } line)
    {
        logger.LogDebug("{Name}: {Line}", fileName, line);
        yield return line;
    }

    var stderr = await stderrTask;
    foreach (var errLine in stderr)
        logger.LogDebug("STDERR {FileName}: {Line}", fileName, errLine);
    // ...
}
```

---

### 6.4 — File-to-track matching with dictionary

```csharp
var tracksByFileName = dbTracks
    .Where(t => !string.IsNullOrEmpty(t.FileName))
    .ToDictionary(t => t.FileName, StringComparer.OrdinalIgnoreCase);

var tracksByNumber = dbTracks
    .Where(t => !string.IsNullOrEmpty(t.TrackNumber))
    .ToDictionary(t => $"t{int.Parse(t.TrackNumber):D2}");

foreach (var file in Directory.EnumerateFiles(makeMkvOutPath, "*.mkv"))
{
    var fileName = Path.GetFileName(file);
    if (tracksByFileName.TryGetValue(fileName, out var track)
        || tracksByNumber.FirstOrDefault(kv => fileName.Contains(kv.Key)).Value is { } t2)
    {
        track ??= t2;
        track.FileName = fileName;
        track.FileSize = new FileInfo(file).Length;
        track.Ripped = true;
    }
}
```

---

## Summary — Priority Order

| # | Issue | Impact | Effort | Stage |
|---|-------|--------|--------|-------|
| 1 | ConfigSnapshot factory method | Eliminates ~300 lines of duplicated fragile code | Low | 2.2 / 5.1 |
| 2 | `RunStreamingAsync` exit-code throw | Prevents error handling for MakeMKV status codes | Low | 1.1 |
| 3 | `StartImportJob` sync-over-async | Removes deadlock risk | Low | 1.3 |
| 4 | Optimistic concurrency on Job entity | Prevents silent state corruption | Medium | 1.5 |
| 5 | `RunStreamingAsync` stderr drain | Prevents process hangs on stderr-heavy runs | Low | 4.2 |
| 6 | Extract `ArmRipperService` into smaller services | Major maintainability win | High | 2.1 |
| 7 | `VideoType` enum instead of strings | Catches typos at compile time | Medium | 2.4 |
| 8 | Config overrides as JSON blob | 10x storage reduction for config | Medium | 3.1 |
| 9 | `CompletedStages` as bitmask | Zero-allocation stage checks | Low | 6.1 |
| 10 | File-match dictionary | O(n²)→O(n) for post-rip file matching | Low | 6.4 |
