# ARM Sharp Ripping Pipeline — Stages & Steps

This document defines the current ripping process stages in ARM Sharp (C#) and identifies the necessary work to support proper end-to-end happy path operation with good UI feedback.

---

## Architecture Overview

The ripping pipeline is orchestrated by `Conductor` (in `ArmRipper.Core/Rip/Conductor.cs`), which dispatches to specialized services based on disc type:

- **IdentifyService** — Disc detection & metadata lookup
- **ArmRipperService** — Video ripping orchestration (DVD/Blu-ray)
- **MusicBrainzService** + abcde — Audio CD ripping
- **dd command** — Data disc ripping
- **MakeMkvService** — Track extraction from video discs
- **HandBrakeService** / **FfmpegService** — Video transcoding

---

## Stage 1: SETUP

**Responsible:** `Conductor.Setup()` + `Conductor.SetupJobAsync()`

**Purpose:** Create job record, initialize directories, prepare config snapshot

**Steps:**
1. Create required directories:
   - `{RawPath}`
   - `{TranscodePath}`
   - `{CompletedPath}`
   - `{LogPath}` and `{LogPath}/progress`

2. Create `Job` record in database
   - Set `Status = JobState.Active`
   - Set `StartTime = DateTime.UtcNow`
   - Set `DiscType = DiscType.Unknown`
   - Save and reload to get auto-generated `Job.Id`

3. Create per-job `ConfigSnapshot`
   - Snapshots all `ArmSettings` at rip time
   - Allows job to complete even if settings change mid-rip
   - Stores: paths, rip method, transcode preset, notification keys, API keys, etc.

4. Log ARM parameters (`LogArmParams()`)

**UI Feedback:**
- Status: "Setting up job..."
- No progress bar (instant)

---

## Stage 2: IDENTIFY DISC

**Responsible:** `IdentifyService.IdentifyAsync()`

**Purpose:** Detect disc type and fetch metadata (title, year, poster)

**Sub-steps:**

### 2.1 Mount Disc
- Check if device (`/dev/sr0`, etc.) is already mounted
- If not:
  - Create mount target directory `/mnt/dev/{devName}`
  - Run `mount --source {device} --target {mountTarget}`
- Extract disc label via `blkid -s LABEL -o value {device}`
- **Duration:** 5-30 seconds

**Current state tracking:**
- No specific `JobState` for mounting (remains `Active`)

### 2.2 Detect Disc Type
- **First attempt:** Check mounted filesystem markers:
  - Audio CD: `AUDIO_TS/` directory
  - Blu-ray: `BDMV/` directory
  - DVD: `VIDEO_TS/` directory
  - Data: unknown/ISO9660 marker

- **Fallback:** Read sector count from sysfs `/sys/block/{devName}/size`:
  - BD: > 15 GB (15_000_000_000 bytes)
  - DVD: > 4 GB but ≤ 15 GB
  - CD: ≤ 4 GB (audio or data)

- **If type unclear:** Default to `DiscType.Unknown` → Job fails at dispatch

**Duration:** < 1 second (filesystem check) or 1-2 seconds (sysfs read)

### 2.3 Fetch Video Metadata (DVD/BD only, optional)
**Condition:** `ArmSettings.GetVideoTitle == true`

- **For DVD:**
  - Call `HandBrakeService.GetTitleAsync()` to list available titles
  - Compute CRC64 of disc structure via `DvdCrc64.Compute()`
  - Lookup CRC64 in ARM API (`ArmSettings.ArmApiKey`)
  - If found: extract title, year, video type from API response
  - If not found: call OMDB/TMDB with disc label as search term

- **For Blu-ray:**
  - Parse `BDMV/index.bdmv` for title info
  - No ARM API lookup for BD
  - Call OMDB/TMDB with disc label

- **Database lookup:**
  - Query `Jobs` table for previous rips with same `Label`
  - If exactly 1 match: inherit title/year/type from previous job
  - If > 1 match: skip (ambiguous)
  - If 0 matches: mark `HasNiceTitle = false`, use label as title, log warning

**Duration:** 2-10 seconds (API calls), cached if previously ripped

**Status transitions:**
- `Active` → `VideoInfo` (if fetching metadata) → `Active`

### 2.4 Compute Disc Fingerprint
- Create fingerprint: `{VolumeLabel}::{SectorCount}`
- Store in `Job.DiscFingerprint`
- Used for caching track info (MakeMKV queries) in `disc_metadata` table

**Duration:** < 1 second

### 2.5 Unmount Disc
- Run `umount {device}` (optional, depends on config)
- Usually left mounted for ripping

**Duration:** < 1 second

**UI Feedback:**
- Status: "Identifying disc..."
- Sub-step: "Mounting disc", "Detecting type", "Fetching metadata"
- **No progress bar** — timing unpredictable (API calls)
- Warning if disc type fallback used
- If metadata not found: "Using disc label as title"

**Current Gaps:**
- ❌ No real-time step feedback to UI
- ❌ No indication of which metadata provider is being tried
- ❌ No retry on transient API failure
- ❌ Unmount failure logged but doesn't fail job

---

## Stage 3: DUPLICATE CHECK & MANUAL OVERRIDE

**Responsible:** `Conductor.ProcessJobAsync()`

**Purpose:** Allow user to verify/override auto-detected title before ripping

### 3.1 Duplicate Check
- Already done in `Stage 2.3` (database lookup)
- If previous rip found: log that we're copying metadata

### 3.2 Manual Wait (optional)
**Condition:** `ConfigSnapshot.ManualWait == true` AND `Job.TitleManual` is empty

- Set `Job.Status = JobState.ManualWaitStarted`
- Poll every 5 seconds for up to `ManualWaitTime` seconds (default 60)
- Check for:
  - `Job.Status == Cancelled` → abort job
  - `Job.TitleManual` populated → use override and continue
- If timeout expires: log warning, continue with auto-detected title
- Reset `Job.Status = Active`

**Duration:** 0-60 seconds (user-dependent)

**UI Feedback:**
- Status: "Waiting for manual title override..."
- Display countdown timer
- Show current auto-detected title + year/type
- Text box to enter override title
- "Continue" / "Cancel" buttons

**Current Gaps:**
- ❌ UI page for manual override not implemented
- ❌ Only one override field; can't override metadata fields separately
- ❌ No "skip this title" option

---

## Stage 4: DISPATCH BY DISC TYPE

**Responsible:** `Conductor.ProcessJobAsync()` (switch statement)

Routes to specialized ripping pipeline based on `Job.DiscType`:

```
DVD/Blu-ray    → Stage 5 (RipVisualMediaAsync)
Audio CD       → Stage 6 (RipMusicAsync)
Data Disc      → Stage 7 (RipDataAsync)
Unknown        → FAIL (log critical error)
```

---

## Stage 5: VIDEO RIP PIPELINE (DVD/Blu-ray)

**Responsible:** `ArmRipperService.RipVisualMediaAsync()`

**Purpose:** Extract video tracks, transcode, move to final location

### 5.1 Identify Eligible Tracks
**Substep:** `GetTrackInfoWithCacheAsync()`

- **Cached lookup:**
  - Query `disc_metadata` table by `Job.DiscFingerprint`
  - If found: deserialize stored track list, skip makemkvcon

- **Live query (fallback):**
  - Run `makemkvcon info disc:{device}`
  - Parse output: title, duration, stream info (video/audio/subs)
  - Store in `disc_metadata` / `disc_tracks` / `disc_track_streams` tables
  - **Duration:** 2-10 seconds per disc (first time), < 1 second (cached)

- **Handle encrypted Blu-rays:**
  - MakeMKV returns 0 tracks for encrypted discs
  - Fallback: `RipAllTitlesAsync()` without track list
  - Run makemkvcon to rip all available titles (0, 1, 2, ...)
  - **Duration:** Unknown (could take 30+ minutes)

- **Filter tracks:**
  - Discard tracks with duration < `MinLength` (default 300 seconds)
  - Discard tracks with duration > `MaxLength` (default 99998 seconds)
  - Mark eligible tracks with `Track.Process = true`

- **Identify main feature:**
  - Find longest eligible track
  - Set `Track.MainFeature = true`

**Status transition:** `Active/VideoInfo` → `VideoRipping`

**UI Feedback:**
- Status: "Reading track information..."
- If cached: "Using cached metadata"
- If live: "Scanning disc..." + elapsed time
- Warn if 0 tracks found (encrypted BD) + "Ripping all titles" fallback
- Display track list once identified

**Current Gaps:**
- ❌ No progress indication during makemkvcon (can take 10+ seconds)
- ❌ No indication that disc is encrypted until 0 tracks returned
- ❌ Can't cancel GetTrackInfo (long-running)
- ❌ User can't see track list to select which to rip

### 5.2 Rip Tracks with MakeMKV
**Substep:** `RipTrackAsync()` or `RipAllTitlesAsync()`

**Choose rip strategy based on config:**

| Condition | Strategy | Rips | Duration |
|-----------|----------|------|----------|
| `MainFeature==true` | Main only | Longest track | 5-30 min |
| `MaxLength > 99998` | All tracks | All eligible | 10-60 min |
| 0 tracks (encrypted BD) | All titles | All (0, 1, 2, ...) | 30+ min |
| Test mode | Track 0 only | Track 0 | < 1 min |
| Otherwise | Each track | All eligible, one-by-one | 10-60 min |

**MakeMKV command:**
```
makemkvcon mkv --minlength=<seconds> --directfile --messages=<logfile> disc:{device} <track> <output>
```

**Output:** MKV files to `{RawPath}/{JobTitle}/title_00.mkv`, `title_01.mkv`, etc.

**Progress tracking:**
- MakeMKV outputs progress lines: `PRGT:... (frames / total)`
- Parsed into `Job.MakeMkvProgress` (0-100%)
- `Job.ProgressMessage = "Ripping track N of M"` or `"Ripping all titles"`
- Updated synchronously (blocks on each frame parsed)

**Duration:** 5-60 minutes depending on disc content and strategy

**Status:** Remains `VideoRipping`

**Test mode special:** Truncate each MKV to 30 seconds with FFmpeg (for testing without long rips)

**UI Feedback:**
- Status: "Ripping disc with MakeMKV"
- Progress bar: `MakeMkvProgress %`
- Sub-message: `ProgressMessage`
- Elapsed time + estimated remaining (based on frame rate)
- Log tail: last 10 lines of MakeMKV output

**Current Gaps:**
- ❌ Progress only updated synchronously (blocks polling)
- ❌ No async progress streaming to UI (no SignalR updates)
- ❌ Can't cancel mid-rip without killing entire job
- ❌ No detail on which track is being ripped
- ❌ Errors from MakeMKV not clearly reported to user

### 5.3 Transcode Video (optional)
**Substep:** `StartTranscodeAsync()`

**Skip if:** `ConfigSnapshot.SkipTranscode == true`

**Choose transcoding engine:**
- FFmpeg (if `UseFfmpeg == true`)
- HandBrake (default)

**Choose transcoding strategy:**
| Scenario | Method | Input | Output |
|----------|--------|-------|--------|
| RipMethod is "mkv" | TranscodeMkvAsync | All MKV files | One file per MKV |
| MainFeature && movie | TranscodeMainFeatureAsync | Longest MKV | Longest.{mp4,mkv} |
| Otherwise | TranscodeAllAsync | All MKV files | One file per MKV |

**Transcoding command (HandBrake example):**
```
HandBrakeCLI -i <input.mkv> -o <output.mp4> \
  --preset "<HbPresetDvd>" <HbArgsDvd> --json 2>/dev/null | jq ...
```

**Output:** Encoded video to `{TranscodePath}/{TypeSubFolder}/{JobTitle}/`

**Progress tracking:**
- HandBrake/FFmpeg output parsed for progress (frame count, ETA)
- `Job.TranscodeProgress` (0-100% per file)
- `Job.ProgressMessage = "Transcoding main feature"` or `"Transcoding all tracks"`
- Overall progress = (current file % + completed files) / total files

**Duration:** 1-6 hours (depending on video quality & transcode preset)

**Status transition:** `VideoRipping` → `TranscodeActive` → `Active` (if success) or `Failure`

**UI Feedback:**
- Status: "Transcoding video"
- Progress bar: `TranscodeProgress %`
- Sub-message: `ProgressMessage` + file name
- Elapsed time + estimated remaining
- Log tail: encoder output (% complete, bitrate, ETA)

**Current Gaps:**
- ❌ No async progress streaming (blocks polling)
- ❌ No indication of how many files total / which file being encoded
- ❌ HandBrake JSON output parsing incomplete (some fields missing)
- ❌ FFmpeg progress not clearly separated per-file
- ❌ No option to skip/abort individual files without killing job

### 5.4 Move Files to Final Location
**Substep:** `MoveFilesPostAsync()`

**Routing logic:**

| VideoType | Condition | Destination |
|-----------|-----------|-------------|
| Series | Always | `{CompletedPath}/series/{Title}/S{s}E{e}_{Filename}` |
| Movie | 1 file | `{CompletedPath}/movies/{Title}/{Filename}` |
| Movie | Multi-file, main feature | `{CompletedPath}/movies/{Title}/{Filename}` |
| Movie | Multi-file, extra file | `{CompletedPath}/movies/{Title}/{ExtrasSub}/{Filename}` |

**Filename construction:**
- If `TitleManual` was set: rename based on manual title + metadata
- Otherwise: use original filename from transcode output
- For series: parse episode info from metadata if available

**Operations:**
1. Create destination directory structure
2. Move (or copy + delete) files from transcode output
3. Update `Track.Path` for each moved file

**Duration:** < 1 minute

**Status:** Remains `Active`

**UI Feedback:**
- Status: "Moving files to final location..."
- File list: "Moving {OriginalName} → {FinalPath}"
- No progress bar (fast)

**Current Gaps:**
- ❌ Series episode naming not fully implemented
- ❌ ExtrasSub naming not clearly documented
- ❌ No user feedback on move operations
- ❌ No "undo move" option if next step fails

### 5.5 Cleanup Temporary Files
**Substep:** `DeleteRawFiles()`

**Deletes:**
- `{RawPath}/{JobTitle}/` (MKV files from rip)
- `{TranscodePath}/{TypeSubFolder}/{JobTitle}/` (encoded files, if moved)
- `makeMkvOutPath` (if using MakeMKV)

**Condition:**
- Deleted only if transcode succeeded OR `SkipTranscode==true`
- If transcode failed: raw files kept for debugging

**Duration:** < 1 minute

**Status:** Remains `Active`

**UI Feedback:**
- Status: "Cleaning up temporary files..."
- No detailed output (background task)

**Current Gaps:**
- ❌ User can't manually trigger cleanup
- ❌ No disk space recovery estimate

### 5.6 Emby Library Refresh (optional)
**Substep:** `ScanEmbyAsync()`

**Condition:** `ArmSettings.EmbyServer` configured + `ArmSettings.EmbyRefresh == true`

**Operation:**
- POST to `http://{EmbyServer}:{EmbyPort}/Library/Refresh?api_key={ApiKey}`
- Triggers Emby to scan new files

**Duration:** 1-5 seconds (API call)

**Status:** Remains `Active`

**UI Feedback:**
- Status: "Refreshing Emby library..."
- No detailed output (background task)

**Current Gaps:**
- ❌ No confirmation of refresh success
- ❌ No indication if Emby is not configured

### 5.7 Set Permissions
**Substep:** `SetPermissions()`

**Operation:**
- `chmod 777 {FinalPath}` (full permissions for web access)

**Duration:** < 1 second

**Status:** Remains `Active`

**UI Feedback:**
- No user feedback

### 5.8 Notify Completion
**Substep:** `NotifyExitAsync()`

**Condition:** `ConfigSnapshot.NotifyTranscode == true`

**Message:**
- Success: `"{Title} processing complete."`
- Failure: `"{Title} processing completed with errors. Title(s) {FailedFiles} failed to complete."`

**Notification channels:**
- Apprise (30+ services: Discord, Slack, Pushbullet, etc.)
- Pushbullet (if `PbKey` set)
- IFTTT (if `IftttKey` set)
- Gotify (if configured)
- Custom bash script (if `BashScript` set)

**Duration:** < 5 seconds

**Status:** Remains `Active`

**UI Feedback:**
- No feedback (notification sent externally)

**Current Gaps:**
- ❌ No confirmation that notification sent
- ❌ No error reporting if notification fails

---

## Stage 6: AUDIO CD RIP

**Responsible:** `Conductor.RipMusicAsync()`

**Purpose:** Rip audio CD and tag tracks

### 6.1 Identify Album & Tracks
**Substep:** `MusicBrainzService.IdentifyAsync()`

- Query MusicBrainz API with disc fingerprint (TOC)
- Return album title + track listing

**Duration:** 1-5 seconds (API call)

### 6.2 Rip with abcde
**Command:**
```bash
abcde -d {device} -c {abcde.conf} >> {logFile} 2>&1
```

**Configuration:**
- Reads from `{InstallPath}/abcde.conf` or `/etc/arm/config/abcde.conf`
- Output format configured in abcde.conf (FLAC, MP3, OGG, etc.)

**Duration:** 5-30 minutes

**Status:** `Active` → `AudioRipping` → `Active`

**UI Feedback:**
- Status: "Ripping audio CD with abcde..."
- No progress bar (abcde output not parsed)
- Log tail: abcde output

**Current Gaps:**
- ❌ No progress tracking from abcde (long-running, no feedback)
- ❌ abcde often hangs; no timeout mechanism
- ❌ Final files location not exposed to DB
- ❌ No Emby integration for music

---

## Stage 7: DATA DISC RIP

**Responsible:** `Conductor.RipDataAsync()`

**Purpose:** Rip data disc as ISO

### 7.1 Create Output Structure
- Source: `{RawPath}/{Label}/`
- Destination: `{CompletedPath}/data/{Label}/{Label}.iso`
- If destination exists: append Unix timestamp to avoid conflicts

### 7.2 Rip with dd
**Command:**
```bash
dd if={device} of={RawPath}/{Label}.part bs=2048 conv=noerror,sync status=progress 2>> {logFile}
```

**Output:** `{RawPath}/{Label}.part` → moved to `{FinalPath}/{Label}.iso`

**Duration:** 5-60 minutes (depends on disc size)

**Status:** `Active` (no intermediate state)

**UI Feedback:**
- Status: "Ripping data disc..."
- No progress bar (dd output not parsed)
- Log tail: dd progress output

**Current Gaps:**
- ❌ No progress tracking from dd
- ❌ No ETA estimate
- ❌ No cleanup if rip fails mid-way

---

## Summary: Current Job Lifecycle States

```
SETUP
  ↓
IDENTIFY (± MANUAL WAIT)
  ↓
DISPATCH
  ├─ DVD/BD → RIPPING → TRANSCODE → MOVE → CLEANUP → SUCCESS/FAILURE
  ├─ Audio CD → AUDIO_RIPPING → SUCCESS/FAILURE
  ├─ Data Disc → DATA_RIPPING → SUCCESS/FAILURE
  └─ Unknown → FAILURE
```

**JobState enum values used:**
- `Active` — default, most stages
- `VideoInfo` — during metadata fetch (Stage 2.3)
- `ManualWaitStarted` — waiting for user override (Stage 3)
- `VideoRipping` — MakeMKV ripping (Stage 5.2)
- `TranscodeActive` — transcode in progress (Stage 5.3)
- `AudioRipping` — abcde ripping (Stage 6)
- `Success` — job completed without errors
- `Failure` — job failed (error set in `Job.Errors`)
- `Cancelled` — user cancelled job

**Unused in current code:**
- `VideoWaiting` — reserved for paused transcode
- `TranscodeWaiting` — reserved for paused transcode

---

## Known Issues for End-to-End Happy Path

### 1. **Weak Progress Reporting**
- ❌ Only MakeMkvProgress and TranscodeProgress tracked
- ❌ No feedback on identify stage duration
- ❌ No overall job progress (X of Y stages complete)
- ❌ No time estimates or remaining time calculations

### 2. **No Real-Time UI Updates During Long Operations**
- ❌ Progress only synced synchronously (blocks on each update)
- ❌ SignalR hub exists but not used for progress streams
- ❌ ffmpeg/HandBrake output not streamed to client
- ❌ abcde/dd output not parsed or streamed

### 3. **Disc Type Detection Fragile**
- ❌ Fallback to sector count heuristic (not reliable for all disc types)
- ❌ Mount can fail silently (tries to extract label anyway)
- ❌ No clear error message if type detection fails

### 4. **Track Selection Not User-Visible**
- ❌ Track list identified but not displayed in UI
- ❌ User can't manually select which tracks to rip
- ❌ Can't exclude specific episodes from series rip

### 5. **Metadata Lookup Not Robust**
- ❌ No retry on transient API failure
- ❌ OMDB/TMDB API calls not distinguished in logs
- ❌ Can't fall back to manual title entry if API fails

### 6. **Manual Override Limited**
- ❌ Only title field overridable
- ❌ Can't override year, type, poster, etc.
- ❌ No "skip this title" option

### 7. **Error Recovery Weak**
- ❌ Errors logged to `Job.Errors` but not actionable
- ❌ No "resume from stage X" mechanism
- ❌ User must restart entire job if mid-stage failure

### 8. **Long Operations Not Cancellable**
- ❌ Can only cancel during manual wait
- ❌ Can't gracefully abort rip/transcode mid-way
- ❌ No "pause for adjustment" before transcode

---

## Recommended Next Steps

### Phase 1: Clarify Stages in UI
1. Add explicit stage enum + stage number to Job model
2. Update UI to show "Stage 2 of 8: Identify Disc" format
3. List all steps within current stage + which step running now

### Phase 2: Add Progress Tracking Infrastructure
1. Add `ProgressStage`, `ProgressStep`, `ProgressPercent`, `ProgressEta` to Job model
2. Create helper to update all four fields atomically
3. Stream updates to UI via SignalR (not just sync DB saves)

### Phase 3: Implement Async Progress Streams
1. Wrap ffmpeg/HandBrake/makemkvcon/abcde/dd output parsers
2. Parse progress lines → emit to `IProgress<int>` (existing pattern)
3. Hook `IProgress` to signal progress updates via SignalR

### Phase 4: Add User Feedback & Control Points
1. Show track list after identification (Stage 5.1)
2. Allow user to select which tracks to rip before Stage 5.2
3. Pause before transcode to allow metadata adjustment
4. Display metadata lookup status (which provider being tried)

### Phase 5: Improve Error Handling & Recovery
1. Break `StageErrors` into structured per-stage records
2. Provide "retry from stage X" option
3. Add explicit error codes + recovery suggestions

This phased approach allows incremental improvement while maintaining backward compatibility.
