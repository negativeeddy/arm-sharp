# Plan: Isolate Raw/Transcode Directories for Multi-Disc TV Series

**Date:** 2026-09-06  
**Status:** Draft  
**Problem:** Multi-disc TV series jobs (S1D1, S1D2) share the same raw rip and transcode output directories, causing files to clobber each other when discs are ripped back-to-back.

---

## 1. Problem Analysis

### What Happens Today

When two discs of the same series are processed, `ComputeRipContextAsync` builds paths using `FixJobTitle(job)` (e.g. `"My Name Is Earl (2005)"`) which is identical for both discs:

```
makeMkvOutPath   = {RawPath}/My Name Is Earl (2005)/          ← S1D1 and S1D2 collide
transcodeOutPath = {TranscodePath}/tv/My Name Is Earl (2005)/ ← S1D1 and S1D2 collide
finalDirectory   = {CompletedPath}/tv/My Name Is Earl (2005)/ ← CheckForDupeFolder appends _{jobId}
```

### Why Files Disappear

1. **During rip**: S1D2 starts ripping while S1D1 is still transcoding from the same raw directory. MakeMKV writes `title_t00.mkv`, `title_t01.mkv`, etc. into `{RawPath}/My Name Is Earl (2005)/`. If S1D2's MakeMKV output filenames overlap with S1D1's (which they will, since MakeMKV uses sequential naming), S1D2's files overwrite S1D1's raw files. If S1D1's transcode later fails and needs retry, the raw files are gone.

2. **During transcode**: Both discs transcode into `{TranscodePath}/tv/My Name Is Earl (2005)/`. The transcode services (ffmpeg/HandBrake) enumerate `*.mkv` files in the raw directory and write output to the transcode output directory. If both are running simultaneously, output files can overwrite each other (e.g. both write `title_t00.mp4`).

3. **After transcode**: `MoveFilesPostAsync` moves files from `transcodeOutPath` to `{CompletedPath}/tv/Series Name/Season XX/SxxExx - Title.ext`. This part is correct — the Season directory is shared and episode numbers ensure uniqueness. But by this point, the damage is already done in the working directories.

### Why `CheckForDupeFolder` Doesn't Fix This

`CheckForDupeFolder` only appends `_{jobId}` to the **final directory** (`completed/`), not to the raw or transcode working directories. And it only triggers when the directory already exists on the filesystem — it's a guard against duplicate runs, not a concurrency mechanism.

---

## 2. Proposed Fix

### Core Idea

For TV series discs, append a **season/disc subdirectory** to the raw rip and transcode output paths so each disc job gets its own isolated working directory. The final Season directory remains shared (files move there after transcode completes).

### Path Changes (TV Series Only)

**Before:**
```
makeMkvOutPath   = {RawPath}/My Name Is Earl (2005)/
transcodeOutPath = {TranscodePath}/tv/My Name Is Earl (2005)/
```

**After:**
```
makeMkvOutPath   = {RawPath}/My Name Is Earl (2005)/S01D01/
transcodeOutPath = {TranscodePath}/tv/My Name Is Earl (2005)/S01D01/
```

For a second disc:
```
makeMkvOutPath   = {RawPath}/My Name Is Earl (2005)/S01D02/
transcodeOutPath = {TranscodePath}/tv/My Name Is Earl (2005)/S01D02/
```

**No change for movies** — movies continue to use `{RawPath}/{jobTitle}/` and `{TranscodePath}/movies/{jobTitle}/` as before.

### Fallback When Season/Disc Unknown

If `job.SeasonNumber` or `job.DiscNumber` are not set (e.g. unidentified series), fall back to `{jobId}` to guarantee uniqueness:
```
makeMkvOutPath   = {RawPath}/My Name Is Earl (2005)/_42/
transcodeOutPath = {TranscodePath}/tv/My Name Is Earl (2005)/_42/
```

---

## 3. Implementation Plan

### Step 1: Add `GetSeriesDiscSubdir` helper to `ArmRipperService`

Add a private method that returns the disc-specific subdirectory name for TV series:

```csharp
/// <summary>
/// Returns a season/disc subdirectory name for TV series (e.g. "S01D02"),
/// a jobId-based fallback when season/disc are unknown, or null for movies.
/// </summary>
private static string? GetSeriesDiscSubdir(Job job)
{
    if (job.VideoType is not (VideoContentType.Series or VideoContentType.Tv))
        return null;

    var season = job.SeasonNumber ?? 1;
    var disc = job.DiscNumber ?? ParseDiscNumber(job.Label);
    
    // If both season and disc are available, use the canonical format
    if (job.SeasonNumber is not null || job.DiscNumber is not null)
        return $"S{season:D2}D{disc:D2}";

    // Fallback: use job ID for guaranteed uniqueness
    return $"_{job.Id}";
}
```

### Step 2: Modify `ComputeRipContextAsync` to use the subdirectory

In `ComputeRipContextAsync`, append the series disc subdirectory to `makeMkvOutPath` and `transcodeOutPath`:

```csharp
// Current code:
var makeMkvOutPath = Path.Combine(
    job.Config?.RawPath ?? ArmPaths.GetRawPath(settings.Value), jobTitle);
var transcodeOutPath = Path.Combine(
    job.Config?.TranscodePath ?? ArmPaths.GetTranscodePath(settings.Value), 
    typeSubFolder, jobTitle);

// New code:
var seriesSubdir = GetSeriesDiscSubdir(job);
var makeMkvOutPath = Path.Combine(
    job.Config?.RawPath ?? ArmPaths.GetRawPath(settings.Value), 
    jobTitle, seriesSubdir ?? "");
var transcodeOutPath = Path.Combine(
    job.Config?.TranscodePath ?? ArmPaths.GetTranscodePath(settings.Value), 
    typeSubFolder, jobTitle, seriesSubdir ?? "");
```

Note: `Path.Combine(path, "")` returns `path` unchanged, so the `?? ""` fallback is safe.

### Step 3: No changes needed to downstream code

The rest of the pipeline works unchanged because:

- **Transcode services** (`FfmpegService`, `HandBrakeService`): Read from `rawInPath` (= `makeMkvOutPath`), write to `transcodeOutPath`. Both are now unique per disc.
- **`MoveFilesPostAsync`**: Reads from `transcodeOutPath`, writes to `{CompletedPath}/tv/Series Name/Season XX/SxxExx - Title.ext`. The final Season directory is shared and episode numbers ensure uniqueness.
- **`CleanupRawFiles`**: Cleans up `ctx.TranscodeInPath`, `ctx.TranscodeOutPath`, and `ctx.MakeMkvOutPath` — all stored in `RipContext`, so the new paths are cleaned up correctly.
- **`CompletedController`**: Recursively scans all directories — subdirectories are found automatically.
- **`CheckForDupeFolder`**: Still works as a safety net for the `finalDirectory` (completed path).

### Step 4: Update existing tests

Update `ArmRipperServicePhaseTests` to verify:
- TV series jobs get season/disc subdirectories in `MakeMkvOutPath` and `TranscodeOutPath`
- Movie jobs remain unchanged
- Fallback to `_{jobId}` when season/disc are unknown

### Step 5: Add new tests

Add tests for:
- `GetSeriesDiscSubdir` with various season/disc combinations
- `GetSeriesDiscSubdir` fallback when season/disc are null
- `GetSeriesDiscSubdir` returns null for movies
- End-to-end path computation for two TV series discs with different season/disc numbers

---

## 4. Edge Cases

### 4.1 Season/Disc Numbers Change Mid-Rip

If the user changes the season or disc number via the WebUI during the rip, the path won't update (the directory is already created). This is acceptable — the user should start a new job with the corrected metadata. The existing `RipContext` is computed once at the start.

### 4.2 DiscDb Maps to Different Season Than Job

If DiscDb identifies tracks as Season 2 but `job.SeasonNumber` is 1, the working directory uses the job's season (S01D01), while the final files go to Season 2's directory (via `track.TrackSeasonNumber` in `MoveFiles`). This is correct — the working directory just needs to be unique, and the final placement uses DiscDb metadata.

### 4.3 Resume After Stop

When a job is resumed, `PrepareTranscodeInputPathAsync` returns `job.DevPath` (the raw path from the DB). The rip stage is already complete, so the working directory is not re-created. The transcode reads from the existing path. This works correctly with the new subdirectory structure.

### 4.4 Forked Transcode

Forked transcode jobs (`RunForkedTranscodeAsync`) set `DevPath = rawDir` (the raw directory path). The forked job skips the rip phase and reads from this path. The new subdirectory structure doesn't affect forked jobs since they use the original job's raw directory.

### 4.5 Multiple Drives Ripping Same Series

If two drives are ripping S1D1 and S1D2 simultaneously, the new paths are already unique (`S01D01/` vs `S01D02/`), so there's no collision.

### 4.6 Re-rip of Same Disc

If the same disc is ripped again (e.g. after a failure), the season/disc subdirectory will be the same. `CheckForDupeFolder` doesn't apply to these working directories, so the re-rip will overwrite the previous raw files. This is acceptable — the re-rip is intentionally replacing the previous attempt.

If we want to prevent this, we could use `_{jobId}` instead of season/disc, but that makes the directory structure less readable. The current behavior (overwrite on re-rip) is consistent with the existing behavior for movies.

---

## 5. Filesystem Impact

### Directory Structure Before
```
/home/arm/media/
├── raw/
│   └── My Name Is Earl (2005)/          ← shared between discs
│       ├── title_t00.mkv
│       └── title_t01.mkv
├── transcode/
│   └── tv/
│       └── My Name Is Earl (2005)/      ← shared between discs
│           ├── title_t00.mp4
│           └── title_t01.mp4
└── completed/
    └── tv/
        └── My Name Is Earl/
            └── Season 01/
                ├── S01E01 - Pilot.mp4
                └── S01E02 - Bread.mp4
```

### Directory Structure After
```
/home/arm/media/
├── raw/
│   └── My Name Is Earl (2005)/
│       ├── S01D01/                      ← isolated per disc
│       │   ├── title_t00.mkv
│       │   └── title_t01.mkv
│       └── S01D02/
│           ├── title_t00.mkv
│           └── title_t01.mkv
├── transcode/
│   └── tv/
│       └── My Name Is Earl (2005)/
│           ├── S01D01/                  ← isolated per disc
│           │   ├── title_t00.mp4
│           │   └── title_t01.mp4
│           └── S01D02/
│               ├── title_t00.mp4
│               └── title_t01.mp4
└── completed/
    └── tv/
        └── My Name Is Earl/
            └── Season 01/
                ├── S01E01 - Pilot.mp4   ← shared, episode numbers ensure uniqueness
                └── S01E05 - South Tower.mp4
```

### Cleanup

After successful transcode and finalize, `CleanupRawFiles` removes the working directories. The season/disc subdirectories under `raw/` and `transcode/` are cleaned up as part of this. Over time, completed working directories are purged.

---

## 6. Summary

| Aspect | Current | Proposed |
|--------|---------|----------|
| Raw rip dir (series) | `{RawPath}/{title}/` | `{RawPath}/{title}/S{SS}D{DD}/` |
| Transcode dir (series) | `{TranscodePath}/tv/{title}/` | `{TranscodePath}/tv/{title}/S{SS}D{DD}/` |
| Raw rip dir (movies) | `{RawPath}/{title}/` | No change |
| Transcode dir (movies) | `{TranscodePath}/movies/{title}/` | No change |
| Final destination | `{CompletedPath}/tv/{series}/Season XX/` | No change |
| Collisions | Yes — files clobber each other | No — each disc has its own directory |

**Files to modify:**
- `src/ArmRipper.Core/Rip/ArmRipperService.cs` — `ComputeRipContextAsync()` + new `GetSeriesDiscSubdir()` helper
- `tests/ArmRipper.Core.Tests/ArmRipperServicePhaseTests.cs` — update and add tests
