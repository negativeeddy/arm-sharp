# ARM Sharp Pipeline Testing Progress

## Current Status
**Date**: 2026-06-14
**Disc**: Zombieland (DVD)
**Jobs**:
- #12 — **FULL PIPELINE SUCCESS** ✅ (14m 10s)
- #13 — **FULL PIPELINE SUCCESS** ✅ (13m 33s)
- #14 — **PIPELINE RUNNING** 🔄 (MakeMKV progress = 66% and climbing)
- ✅ **UI timezone fix applied** — All UTC timestamps now converted to local time (America/Chicago) via `.ToLocalTime()` in 7 view files

**Output**:
- Job #12: `/home/arm/media/completed/movies/Zombieland (2009)/Zombieland (2009).mkv` (2.1GB)
- Job #13: `/home/arm/media/completed/movies/Zombieland (2009)_1781401312/Zombieland (2009).mkv` (2.1GB)

### Fixed and Validated
1. ✅ **IdentifyLoopAsync return type** — Changed `Task` to `Task<JsonDocument?>` so OMDB/TMDB response is actually returned and parsed
2. ✅ **OMDB metadata parsing** — VideoType, Year, IMDb ID, Poster now extracted from search results (previously discarded)
3. ✅ **Mount retry** — 3-attempt loop with `eject -t` tray re-seat when mount fails with "no medium found"
4. ✅ **Progress callback threading** — Replaced `Progress<T>` with `InlineProgress<T>` to avoid SynchronizationContext/ThreadPool dispatch issues
5. ✅ **Local tablesorter** — jquery.tablesorter CSS/JS now served locally (CDN was blocked by MIME type checks)
6. ✅ **Continue button** — ManualWait stage has a Continue button to skip the 60s wait
7. ✅ **HandBrake progress parsing** — `ParseHandBrakeProgress` now called on both stdout and stderr (since `2>&1` redirect sends progress to stdout). **Validated with Job #13**: TranscodeProgress updated live: 15% → 39% → 66% → 89% → 100%
8. ✅ **MakeMkvProgress file-size estimation** — Added fallback progress estimation by monitoring output .mkv file growth against expected track size. **Validated with Job #14**: MakeMkvProgress updated live: 3% → 5% → 8%+ during rip phase.

## Pipeline Achievements
- ✅ Full pipeline: Setup → Identify → ManualWait → Rip → Transcode → Finalize
- ✅ **Live TranscodeProgress tracking in DB** (was always null before fix)
- ✅ **Live MakeMkvProgress tracking in DB** (was always null before fix)
- ✅ Continue button on ManualWait stage
- ✅ Tray reseat (eject -t) for mount failures
- ✅ Local tablesorter files (no CDN dependency)
- ✅ Auto-refresh on JobDetail page (5s for active jobs)
- ✅ Metadata correctly extracted from OMDB
- ✅ Output routing by VideoType (movies/, tv/, unidentified/)
- ✅ Raw/transcode temp files cleaned up after completion
- ✅ BackgroundRipService reports completion
- ✅ Deduplication via `_unixTimestamp` suffix works correctly

## Known Issues
1. ✅ **MakeMkvProgress always null** — **FIXED** by adding file-size based progress estimation (background monitor polls output .mkv size vs expected track size)
2. **Job.Stage field** — Currently a Unix timestamp, not a descriptive stage name. Used only for deduplication in output paths.
3. **SignalR hub** — Exists for log streaming but doesn't broadcast job progress updates. Adding live progress would require hub method + client-side SignalR subscription.
4. **Audio CD / Data disc** — Pipelines exist in Conductor but untested.
5. **Notification services** — Pushbullet, IFTTT, Pushover configured in appsettings but untested.
