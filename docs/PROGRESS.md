# ARM Sharp Pipeline Testing Progress

## Current Status
**Date**: 2026-06-13
**Disc**: Zombieland (DVD)
**Job**: #12 — **FULL PIPELINE SUCCESS** ✅
**Status**: Success (14m 10s total)
**Output**: `/home/arm/media/completed/movies/Zombieland (2009)/Zombieland (2009).mkv` (2.1GB)

### Fixed in this session
1. ✅ **IdentifyLoopAsync return type** — Changed `Task` to `Task<JsonDocument?>` so OMDB/TMDB response is actually returned and parsed
2. ✅ **OMDB metadata parsing** — VideoType, Year, IMDb ID, Poster now extracted from search results (previously discarded)
3. ✅ **Mount retry** — 3-attempt loop with `eject -t` tray re-seat when mount fails with "no medium found"
4. ✅ **Progress callback threading** — Replaced `Progress<T>` with `InlineProgress<T>` to avoid SynchronizationContext/ThreadPool dispatch issues
5. ✅ **Local tablesorter** — jquery.tablesorter CSS/JS now served locally (CDN was blocked by MIME type checks)
6. ✅ **Continue button** — ManualWait stage has a Continue button to skip the 60s wait
7. ✅ **InlineProgress fix validated** — Pipeline completes correctly; progress values are set but MakeMKV/HandBrake may not emit parseable progress lines

## Pipeline Achievements
- ✅ Full pipeline: Setup → Identify → ManualWait → Rip → Transcode → Finalize
- ✅ Continue button on ManualWait stage
- ✅ Tray reseat (eject -t) for mount failures
- ✅ Local tablesorter files (no CDN dependency)
- ✅ Auto-refresh on JobDetail page (5s for active jobs)
- ✅ Metadata correctly extracted from OMDB
- ✅ Output routing by VideoType (movies/, tv/, unidentified/)
- ✅ Raw/transcode temp files cleaned up after completion
- ✅ BackgroundRipService reports completion

## Known Issues
1. **MakeMkvProgress/TranscodeProgress always null** — Progress callbacks are invoked but MakeMKV may not output PRGC/PRGV lines during rip, and HandBrake's progress goes through `2>&1` redirect which puts it on stdout instead of stderr (where parsing looks). The ProgressMessage IS set correctly ("Transcoding file 1 of 1").
2. **Job.Stage field** — Currently a Unix timestamp, not a descriptive stage name. Used only for deduplication in output paths.
3. **SignalR hub** — Exists for log streaming but doesn't broadcast job progress updates.
4. **Audio CD / Data disc** — Pipelines exist in Conductor but untested.
5. **Notification services** — Pushbullet, IFTTT, Pushover configured in appsettings but untested.
