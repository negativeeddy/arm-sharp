# ARM Sharp Ripping Pipeline — Visual Stage Map

## End-to-End DVD Rip (Happy Path)

```
┌─────────────────────────────────────────────────────────────────────┐
│ STAGE 1: SETUP (5 seconds)                                          │
│ ├─ Create directories (raw, transcode, completed, logs)             │
│ ├─ Create Job record in database                                    │
│ ├─ Snapshot ConfigSnapshot (all ArmSettings)                        │
│ └─ Log ARM parameters                                               │
│ Status: Active                                                       │
└─────────────────────────────────────────────────────────────────────┘
                              ↓
┌─────────────────────────────────────────────────────────────────────┐
│ STAGE 2: IDENTIFY DISC (10-20 seconds, or 2-10s if cached)         │
│ ├─ Mount disc (if not mounted)                 [5 sec]              │
│ ├─ Extract disc label via blkid                 [1 sec]              │
│ ├─ Detect disc type (filesystem or sysfs)       [1 sec]              │
│ ├─ [IF VIDEO] Fetch metadata via ARM API/OMDB   [5-10 sec] {cached} │
│ │   ├─ Compute CRC64 (DVD)                                          │
│ │   ├─ Lookup ARM API or fallback to OMDB/TMDB                      │
│ │   └─ Check for duplicate rips in DB                               │
│ ├─ Compute disc fingerprint {Label}::{Sectors}  [1 sec]              │
│ └─ Unmount disc                                 [1 sec]              │
│ Status: Active → VideoInfo (if fetching) → Active                   │
└─────────────────────────────────────────────────────────────────────┘
                              ↓
┌─────────────────────────────────────────────────────────────────────┐
│ STAGE 3: MANUAL OVERRIDE (0-60 seconds, optional)                   │
│ ├─ Wait for user to enter manual title        [0-60 sec] {optional} │
│ └─ Continue with auto-detected title if timeout expires             │
│ Status: ManualWaitStarted                                           │
└─────────────────────────────────────────────────────────────────────┘
                              ↓
┌─────────────────────────────────────────────────────────────────────┐
│ STAGE 4: DISPATCH BY DISC TYPE                                      │
│ ├─ DVD/Blu-ray    → STAGE 5 (RipVisualMediaAsync)                   │
│ ├─ Audio CD       → STAGE 6 (RipMusicAsync)                         │
│ └─ Data Disc      → STAGE 7 (RipDataAsync)                          │
└─────────────────────────────────────────────────────────────────────┘
                              ↓
┌─────────────────────────────────────────────────────────────────────┐
│ STAGE 5: VIDEO RIP PIPELINE (DVD/Blu-ray) [30-120 min total]        │
│                                                                       │
│ 5.1 IDENTIFY TRACKS (2-10 seconds)                                   │
│ ├─ Query makemkvcon info disc:{device}         [2-10 sec]           │
│ ├─ Parse tracks: title, length, streams                             │
│ ├─ Filter by MinLength/MaxLength                                    │
│ ├─ Identify main feature (longest track)                            │
│ └─ Save track list to database                                      │
│ Status: Active/VideoInfo → VideoRipping                             │
│                                                                       │
│ 5.2 RIP TRACKS WITH MAKEMKV (10-60 min)                              │
│ ├─ Choose strategy:                                                 │
│ │   ├─ MainFeature=true  → rip longest track only        [5-30 min] │
│ │   ├─ MaxLength>99998   → rip all tracks                [10-60 min]│
│ │   └─ Otherwise         → rip each track 1-by-1         [10-60 min]│
│ ├─ [IF encrypted BD] Fallback: RipAllTitles (0, 1, 2, ...) [30+ min]│
│ └─ Output: /raw/{JobTitle}/title_00.mkv, title_01.mkv, ...          │
│ Progress: MakeMkvProgress (0-100%), ProgressMessage                  │
│ Status: VideoRipping                                                │
│                                                                       │
│ 5.3 TRANSCODE [OPTIONAL if SkipTranscode=false] (20-120 min)         │
│ ├─ Choose engine: FFmpeg or HandBrake                               │
│ ├─ Choose strategy:                                                 │
│ │   ├─ RipMethod="mkv" → transcode all MKV files                    │
│ │   ├─ MainFeature=true → transcode longest only                    │
│ │   └─ Otherwise        → transcode all files                       │
│ ├─ Preset: HbPresetDvd or HbPresetBd (HandBrake)                    │
│ └─ Output: /transcode/{TypeSubFolder}/{JobTitle}/{files}.mp4/mkv    │
│ Progress: TranscodeProgress (0-100%), ProgressMessage               │
│ Status: TranscodeActive → Active (if success) / Failure              │
│                                                                       │
│ 5.4 MOVE FILES TO FINAL LOCATION (< 1 minute)                        │
│ ├─ Series:      {CompletedPath}/series/{Title}/S{s}E{e}_{file}      │
│ ├─ Movie:       {CompletedPath}/movies/{Title}/{file}                │
│ ├─ Movie+Extra: {CompletedPath}/movies/{Title}/{ExtrasSub}/{file}   │
│ └─ Update Track.Path for each file                                  │
│ Status: Active                                                       │
│                                                                       │
│ 5.5 CLEANUP (< 1 minute)                                            │
│ ├─ Delete /raw/{JobTitle}/ (MKV files)                              │
│ ├─ Delete /transcode/{TypeSubFolder}/{JobTitle}/ (if moved)         │
│ └─ Free up disk space                                               │
│ Status: Active                                                       │
│                                                                       │
│ 5.6 EMBY REFRESH [OPTIONAL] (< 5 seconds)                            │
│ ├─ POST /Library/Refresh?api_key=...                                │
│ └─ Trigger Emby to scan new files                                   │
│ Status: Active                                                       │
│                                                                       │
│ 5.7 SET PERMISSIONS (< 1 second)                                     │
│ └─ chmod 777 {FinalPath}                                            │
│                                                                       │
│ 5.8 NOTIFY COMPLETION (< 5 seconds)                                  │
│ └─ Apprise/Pushbullet/Discord/Slack notification                    │
│                                                                       │
└─────────────────────────────────────────────────────────────────────┘
                              ↓
┌─────────────────────────────────────────────────────────────────────┐
│ STAGE 8: FINALIZE                                                   │
│ ├─ Set Status = Success (if no errors)                              │
│ ├─ Set StopTime = DateTime.UtcNow                                   │
│ ├─ Calculate JobLength (hh:mm:ss)                                   │
│ └─ Log: "ARM processing complete"                                   │
└─────────────────────────────────────────────────────────────────────┘
```

---

## Stage Duration Estimates (Happy Path)

| Stage | Duration | Notes |
|-------|----------|-------|
| 1. Setup | 5 sec | Fast |
| 2. Identify | 10-20 sec | 2-10 sec if metadata cached |
| 3. Manual Override | 0-60 sec | Optional, user-dependent |
| 5.1 Identify Tracks | 2-10 sec | Cached after first run |
| 5.2 Rip with MakeMKV | 10-60 min | Depends on disc content |
| 5.3 Transcode | 20-120 min | Depends on video quality |
| 5.4-5.8 Finish | 2-5 min | Cleanup, move, notify |
| **TOTAL** | **1-4 hours** | Mostly transcode time |

**Fastest path:** Skip transcode + use cached metadata = 15-70 minutes
**Slowest path:** Full transcode + uncached metadata = 3-5 hours

---

## Alternative Flows

### Audio CD Flow
```
SETUP → IDENTIFY → [MANUAL OVERRIDE?] → MusicBrainz Lookup 
  → RIPPING (abcde) → SUCCESS/FAILURE
```
**Duration:** 5-30 minutes

### Data Disc Flow
```
SETUP → IDENTIFY → [MANUAL OVERRIDE?] → RIP (dd) → SUCCESS/FAILURE
```
**Duration:** 5-60 minutes

---

## Current JobState Transitions

```
                    ┌─→ VideoInfo (metadata fetch) ──┐
                    │                                 ↓
Setup → Active ─────┤                              Active
(Job    |           ├─→ ManualWaitStarted (user input) ──┤
created)|           │                                    ↓
        └──────────┐│                                  Active
                   └┼──→ VideoRipping (MakeMKV) ─────────┤
                    │                                     ↓
                    ├──→ TranscodeActive (HandBrake/FFmpeg) ─┤
                    │                                        ↓
                    ├──→ AudioRipping (abcde) ───────────────┤
                    │                                        ↓
                    └─────────────────────────────────────→ Success
                                                     or ↓
                                                   Failure
```

---

## Critical Path Analysis (DVD, MainFeature=true, SkipTranscode=false)

**Bottleneck:** Transcode stage (20-120 min, 70% of total time)

### Optimization Opportunities
1. **Parallel transcoding:** Encode multiple files simultaneously (if hardware allows)
   - Currently: Sequential transcoding (one file at a time)
   - Future: Use `MaxConcurrentTranscodes` setting

2. **GPU acceleration:** Use GPU for transcoding
   - HandBrake supports NVIDIA NVENC, AMD VCE, Intel QSV
   - Current: CPU-only by default

3. **Preset optimization:** Balance quality vs. speed
   - Current presets: "Very Fast 1080p30", "Super Fast 720p24", etc.
   - Faster preset = faster transcode but lower quality

4. **Skip transcode:** If format already suitable
   - Set `SkipTranscode = true` → direct move (saves 20-120 min)
   - Output: MKV (direct from MakeMKV, no re-encoding)

5. **Main feature only:** Skip extra features/episodes
   - Set `MainFeature = true` → rip longest track only (saves 30-90 min)

---

## Known Failure Points

1. **Disc mount fails** → Job fails immediately (no error recovery)
2. **Disc type detection fails** → Defaults to Unknown → Job fails
3. **MakeMKV returns 0 tracks (encrypted BD)** → Fallback to RipAllTitles (long-running, unconfirmed)
4. **Metadata lookup fails** → Falls back to disc label as title (warning logged)
5. **Transcode fails** → Files left in /transcode (cleanup skipped) for debugging
6. **Move fails** → Files left in /transcode (not moved to final location)
7. **Emby refresh fails** → Job still succeeds (non-critical)
8. **Notification fails** → Job still succeeds (non-critical)

---

## Recommended UX Improvements

### For Each Stage, Display to User:
1. **Stage name & number** (e.g., "Stage 5.2: Rip Tracks with MakeMKV")
2. **Current step within stage** (e.g., "Ripping track 1 of 3")
3. **Progress bar** (0-100% or indeterminate if ETA unknown)
4. **Elapsed time** (e.g., "Elapsed: 15:23")
5. **Estimated remaining time** (e.g., "Est. remaining: 45:00")
6. **Live log output** (last 10 lines, auto-scrolling)
7. **Pause/cancel buttons** (at appropriate stages)
8. **Error message** if stage fails (with recovery option)

### UI Wireframe (Conceptual)
```
┌────────────────────────────────────────────────────────────────┐
│ Job: Spy Game (2001) — DVD                 [Cancel]            │
├────────────────────────────────────────────────────────────────┤
│                                                                 │
│ Stage 5 of 8: Video Rip Pipeline                               │
│   └─ Currently: Ripping tracks with MakeMKV                    │
│                                                                 │
│ Progress: Ripping track 1 of 3                                 │
│ ██████████░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░ 42%              │
│                                                                 │
│ Elapsed: 12:34                    Est. remaining: 17:26         │
│                                                                 │
│ Live Log Output:                                               │
│   [12:34:56] makemkvcon: Processing title 1...                 │
│   [12:34:57] makemkvcon: Reading frames: 1234/5678 (22%)       │
│   [12:35:02] makemkvcon: Saving: title_00.mkv                  │
│                                                                 │
└────────────────────────────────────────────────────────────────┘
```
