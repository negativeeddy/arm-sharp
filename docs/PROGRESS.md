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
2. ✅ **Job.Stage field** — **FIXED** — Now accurately described as storing descriptive stage names ("setup", "identify", "rip", "transcode", "finalize", "done"). Gaps addressed:
   - `Conductor.SetupJobAsync()` now sets `job.Stage = "setup"`
   - `Conductor.ProcessJobAsync()` now sets `job.Stage = "identify"` before identify call
   - `RipMusicAsync()` now sets `job.Stage = "rip"` before abcde and `job.Stage = "done"` after
   - `RipDataAsync()` now sets `job.Stage = "rip"` before dd and `job.Stage = "done"` after
   - `_Pipeline.cshtml` now dynamically adjusts stages based on `DiscType` (audio/data discs omit transcode stage)
3. 🔄 **SignalR hub — Broadcast job progress updates** — **Phase 2 complete**
   - ✅ `JobUpdate` DTO created in Core.Models — UI-independent snapshot of mutable job fields
   - ✅ `BroadcastJobUpdateAsync(JobUpdate)` added to `INotificationBroadcaster`
   - ✅ Implemented in `SignalRNotificationBroadcaster` (sends "JobUpdate" SignalR event to all clients)
   - ✅ `NullNotificationBroadcaster` implements no-op (used by CLI)
   - ✅ `ArmRipperService` broadcasts on every % progress change (rip + transcode) and status/stage transitions
   - ✅ `Conductor` broadcasts on all status/stage transitions (setup, identify, manual wait, dispatch, audio/data rip, completion)
   - ✅ `common.js` added callback registry — pages register via `arm.onJobUpdate(fn)`
   - ✅ **JobDetail page**: 5s `location.reload()` replaced with SignalR listener that updates progress bars, status, stage, pipeline, stop time, duration, warnings/errors in-place; full reload only on job completion
   - ✅ **Home page**: 10s polling replaced with debounced SignalR-driven refresh (500ms debounce), with 30s fallback polling if SignalR disconnects
   - ✅ `/api/jobs/{id}/pipeline` endpoint added for live pipeline partial re-rendering
   - 🔜 **Phase 3**: Individual cell-level updates on Home page table (currently server-renders tbody on each update)
4. 🔄 **Audio CD / Data disc** — Pipelines exist in Conductor (MusicBrainzService + abcde for audio, dd for data) but untested end-to-end. Stage assignments now wired. Deferred for later — needs physical audio CD and data disc for testing.
5. 📝 **Notification services** — **DOCUMENTED** — Pushbullet, IFTTT, JSON webhook, and Bash scripts are fully implemented in `NotificationService`. Pushover and Apprise are documented in `docs/IMPROVEMENTS.md` for later (low priority — needs real API keys to test). Settings UI Apprise tab is currently read-only.
