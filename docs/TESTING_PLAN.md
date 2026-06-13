# Stage-by-Stage Testing Plan — Zombieland DVD

## Approach
Work through the pipeline **one stage at a time**, verifying each stage produces correct output before moving to the next. If a stage fails, we fix it before proceeding. Manual rip kick-off via the Web UI — no auto-start.

**Test disc:** Zombieland (2009) — 89 min DVD, should detect cleanly.

---

## Stage 1: SETUP — "Create the job"

**What happens:**
- Conductor receives device path (`/dev/sr0`)
- Creates `Job` record in DB
- Snapshots `ConfigSnapshot` from `ArmSettings`
- Creates directories (raw, transcode, completed, logs)
- Logs ARM parameters

**How to test:**
1. Submit a manual rip via the Web UI — enter device path and click "Start Rip"
2. Check the DB: `sqlite3 /etc/arm/config/arm.db "SELECT id, status, dev_path, title, label FROM jobs;"`
3. Verify:
   - A job row exists with `status = 'active'`
   - `ConfigSnapshot` exists linked to that job
   - Log file path is assigned

**Expected result:** Job appears in the Web UI job list with status "Active". Nothing happens next until Stage 2 fires.

**Likely issues:**
- Web UI might not have a "start rip" button wired up yet
- `BackgroundRipService` might not be triggering properly
- Device path might not be passed correctly from the UI form

---

## Stage 2: IDENTIFY — "What disc is this?"

**What happens:**
1. Check if disc is already mounted → if not, `mount /dev/sr0 /mnt/dev/sr0`
2. Extract label via `blkid -s LABEL -o value /dev/sr0`
3. Detect disc type: look for `VIDEO_TS/` directory → **DVD**
4. Since `GetVideoTitle` is enabled:
   - Run `HandBrakeCLI -i /dev/sr0 --title 0` to list available titles
   - Compute CRC64 of disc structure (uses `DvdCrc64.Compute()`)
   - Look up CRC64 in ARM API (`https://1337server.pythonanywhere.com/api/v1/?mode=s&crc64=...`)
   - If ARM API succeeds: extract title="Zombieland", year="2009", video_type="movie"
   - If ARM API fails: fallback to OMDB/TMDB search by disc label
   - If all API fails: use disc label as title, `HasNiceTitle = false`
5. Check for duplicate jobs in DB
6. Compute disc fingerprint: `{ZOMBIELAND}::{sector_count}`
7. Unmount disc (optional)

**How to test:**
1. Watch the Web UI job detail page — stage should update to "Identifying disc..."
2. Check DB after identify completes: `SELECT id, title, year, video_type, disc_type, has_nice_title, crc_id, disc_fingerprint FROM jobs;`
3. Verify:
   - `disc_type = 'Dvd'`
   - `title` is set (hopefully "Zombieland")
   - `year` is set ("2009")
   - `video_type` is set ("movie")
   - `has_nice_title = 1` (if APIs worked)
   - `crc_id` is non-null (CRC64 hash)
   - `disc_fingerprint` is `{ZOMBIELAND}::{sectors}`
4. Check logs for: `"Disc identified as video"`, CRC64 hash, title resolution

**Expected result:** Job shows title "Zombieland (2009)" in the Web UI. Job status transitions `Active → VideoInfo → Active`. Then proceeds to Manual Wait (if enabled) or Dispatch.

**Likely issues:**
- **Mount fails:** dev container might not have privilege to mount. The container needs `--privileged` or specific `--device` mounts for `/dev/sr0`. We might need to manually mount the disc first.
- **CRC64 fails:** `DvdCrc64.Compute()` reads raw block device — needs permission or might not work on a mounted filesystem
- **ARM API fails:** The 1337server API might be down/slow. In that case, falls back to OMDB/TMDB which needs API keys configured
- **OMDB/TMDB fails:** If no API keys are set, falls back to disc label as title
- **Unmount fails:** Non-critical, logged as debug

---

## Stage 3: MANUAL OVERRIDE (if enabled)

**What happens:**
- If `ManualWait = true` in config: job enters `ManualWaitStarted` state
- UI shows the detected title + a "Continue" button
- User can:
  - Edit the title and submit (calls `update-identification`)
  - Click "Continue" to accept the auto-detected title
  - Wait 60s for timeout
- After override: status resets to `Active`

**How to test:**
1. On the job detail page, look for the "Manual Override" panel
2. If title looks wrong, edit it and submit
3. If title looks right, click "Continue"
4. Watch the job status change back to "Active"

**Expected result:** Job proceeds to dispatch. If timeout expires, warning is logged.

**Likely issues:**
- The Continue button we just added might have styling/placement issues
- The `update-identification` form might not surface well in the UI

---

## Stage 4: DISPATCH — "Route to video pipeline"

**What happens:**
- `Conductor` checks `job.DiscType == Dvd`
- Calls `ArmRipperService.RipVisualMediaAsync()`
- This is where the heavy lifting starts

**How to test:**
1. After manual override (or immediately if disabled), watch the job status
2. Status should change from `Active` to `VideoInfo` (track identification)
3. Check logs for: `"Disc identified as video. Starting rip."`

**Expected result:** Job enters the video rip pipeline. If it crashes here, the dispatch routing itself is broken.

**Likely issues:**
- Low risk — this is just a switch statement and a method call

---

## Stage 5.1: TRACK IDENTIFICATION — "What's on the disc?"

**What happens:**
1. `MakeMkvService.GetTrackInfoWithCacheAsync()` called
2. Checks `disc_metadata` cache for fingerprint — if exists, loads cached tracks
3. If not cached: runs `makemkvcon info disc:0 --minlength=...` to enumerate titles
4. Parses output: title number, duration (seconds), chapters, streams (video/audio/sub)
5. Filters by `MinLength`/`MaxLength` (default 300s-99998s)
6. Identifies "main feature" as the longest eligible track
7. Saves tracks to DB with `Track.Process` and `Track.MainFeature` flags

**How to test:**
1. Watch logs for: `"Getting track info from MakeMKV"`, track list output
2. After complete, query: `SELECT * FROM tracks WHERE job_id = <id>;`
3. Verify:
   - At least one track exists (should be ~89 min for Zombieland)
   - The ~89 min track has `process = 1` and `main_feature = 1`
   - Shorter tracks (trailers, menus) have `process = 0`
   - `disc_metadata` table now has an entry for this fingerprint

**Expected result:** Track list populated in DB. Job status transitions `VideoInfo → VideoRipping`.

**Likely issues:**
- **MakeMKV not installed:** The `arm-dependencies:1.7.3` base image should have it, but verify with `which makemkvcon`
- **MakeMKV beta key expired:** The `EnsureKeyAsync()` method auto-fetches the beta key, but the forum URL or API might change. Check logs for key registration
- **makemkvcon fails on the device:** Might need the device passed differently (raw device vs mounted point)
- **Caching works:** Second run should be instant (from cache)
- **0 tracks returned:** Would fall back to `RipAllTitles` — this is the encrypted BD path. For standard DVD, this shouldn't happen.

---

## Stage 5.2: RIP WITH MAKEMKV — "Extract the video"

**What happens:**
1. Based on config strategy:
   - `MainFeature = true` → rip only the longest track (~89 min)
   - Otherwise → rip all eligible tracks
2. Runs: `makemkvcon mkv --minlength=<seconds> --directfile disc:0 <track> <output_dir>`
3. Progress tracked in `Job.MakeMkvProgress` (parsed from MakeMKV output lines)
4. Output: `{RawPath}/Zombieland (2009)/title_00.mkv` (or `title_t01.mkv`)

**How to test:**
1. Watch the job detail page — progress bar should update during rip
2. Check `SELECT make_mkv_progress, progress_message FROM jobs WHERE id = <id>;`
3. Look for output files: `ls -la /home/arm/media/raw/"Zombieland (2009)/"`
4. Verify the MKV file exists and has a reasonable size (Zombieland 89 min → ~2-5GB MKV)

**Expected result:** MKV file(s) in raw path. `MakeMkvProgress` reaches 100%.

**Likely issues:**
- **MakeMKV takes too long:** 89 min movie = ~20-40 min rip time. Need patience.
- **MakeMKV crashes mid-rip:** Check logs for error output. Might be permission/device issue.
- **Output path is wrong:** `{RawPath}` default is `/home/arm/media/raw`. If the directory doesn't exist or isn't writable, rip fails.
- **MakeMKV progress parsing fails:** We parse `PRGV:title,current,total` lines. If the format differs, progress stays at 0.
- **Disc has copy protection:** Most commercial DVDs have CSS encryption. MakeMKV handles this, but it adds time. Check protection flag.

---

## Stage 5.3: TRANSCODE (Skip if testing raw MKV flow)

**Two paths:** This is optional. If `SkipTranscode = true`, we skip directly to file move.

**If not skipping:**
1. Job status: `VideoRipping → TranscodeActive`
2. Choose engine: `HandBrakeCLI` (default) or `ffmpeg`
3. Strategy depends on config:
   - `RipMethod = "mkv"` → `TranscodeMkvAsync()` (transcode each MKV)
   - OR `MainFeature = true` → `TranscodeMainFeatureAsync()` (transcode only the movie)
   - OR → `TranscodeAllAsync()`
4. Output: `{TranscodePath}/movies/Zombieland (2009)/Zombieland (2009).mp4`

**How to test:**
1. During transcode: watch progress bar (`TranscodeProgress`)
2. Check: `SELECT transcode_progress, progress_message FROM jobs WHERE id = <id>;`
3. After complete: `ls -la /home/arm/media/transcode/movies/"Zombieland (2009)/"`
4. Verify output is playable: `ffprobe <output_file>` (if ffmpeg is available)

**Expected result:** MP4/MKV file in transcode path. Status resets to `Active`.

**Likely issues:**
- **HandBrakeCLI not found:** Base image should have it. Verify with `which HandBrakeCLI`.
- **HandBrake preset missing:** Default preset "Very Fast 1080p30" must exist. Check with `HandBrakeCLI --preset-list`.
- **Transcode is SLOW:** 89 min movie at "Very Fast 1080p30" = ~30-90 min. For testing, consider using "Super Fast 720p24" or even "Ultra Fast 1080p30".
- **FFmpeg path:** If `UseFfmpeg = true`, ffmpeg must be on PATH and args must be correct. The FFmpeg service has different command-line arguments than HandBrake.
- **Output filename:** If `DestExt = "mp4"`, output is `.mp4`. If not set, defaults may differ.

---

## Stage 5.4: MOVE FILES — "Put it in its home"

**What happens:**
1. `MoveFilesPostAsync()` runs after transcode (or after rip if skip transcode)
2. For movie: files go to `{CompletedPath}/movies/Zombieland (2009)/`
3. Main feature → `Zombieland (2009).mp4`
4. Extra features → `{ExtrasSub}/` folder (if configured)
5. Files are moved (not copied) from transcode/raw path

**How to test:**
1. After transcode completes, watch for: `"Moving movies Zombieland (2009).mp4 to..."` in logs
2. Check: `ls -la /home/arm/media/movies/"Zombieland (2009)/"`
3. Verify file exists and has expected name

**Expected result:** Movie file in final location. Source files removed from transcode/raw.

**Likely issues:**
- **File rename fails:** `FixJobTitle()` constructs the output name. If `Title` or `Year` is missing, it falls back to "unknown".
- **Move instead of copy:** The code uses `File.Move()` which is the same as rename on same filesystem. If transcode and completed are on different filesystems, this might throw. (But both paths use `/home/arm/media/...` so likely same filesystem.)
- **Duplicate prevention:** `AllowDuplicates` config — if a folder already exists, it appends the timestamp suffix. Check if this triggers unexpectedly.
- **Empty directory check:** The finalizer checks `Directory.EnumerateFileSystemEntries(job.Path).Any()` — if empty, marks as Failure.

---

## Stage 5.5: CLEANUP — "Tidy up"

**What happens:**
- Deletes raw MKV files from `{RawPath}/Zombieland (2009)/`
- Deletes transcode intermediates from `{TranscodePath}/movies/Zombieland (2009)/`
- Non-critical — errors are logged but don't fail the job

**How to test:**
1. Verify raw directories are deleted after move
2. Check logs for: `"Removing raw path - /home/arm/media/raw/Zombieland (2009)"`

**Expected result:** No leftover temp files. Disk space reclaimed.

---

## Stage 5.6-5.8: OPTIONAL STUFF — "Emby, Permissions, Notify"

**What happens:**
- **Emby refresh:** If configured, POST to Emby API
- **Permissions:** `chmod 777` on final directory
- **Notify:** Send Apprise/Pushbullet/Discord notification

**How to test:**
- These are non-critical. If they fail, the job still succeeds.
- Verify in logs: "Permissions set successfully" / "Emby Library Scan request successful" / notification sent

---

## Stage 8: FINALIZE — "Done"

**What happens:**
- `job.Status = Success`
- `job.StopTime` recorded
- `job.JobLength` computed (e.g., "1:15:30")
- Log: "ARM processing complete"

**How to test:**
1. Check job status in UI — should show "Success" with green badge
2. `SELECT status, job_length FROM jobs WHERE id = <id>;`
3. Movie file should be playable in `/home/arm/media/movies/Zombieland (2009)/`

---

## Testing Order & Validation Checklist

```
[ ] Stage 1: Job created in DB, shown in UI
[ ] Stage 2: Disc identified as DVD "Zombieland (2009)"
[ ] Stage 3: Manual override works (Continue button, title edit)
[ ] Stage 4: Dispatch to video pipeline
[ ] Stage 5.1: Tracks identified, ~89 min main feature
[ ] Stage 5.2: MKV file ripped, ~2-5GB file in raw/ 
[ ] Stage 5.3: Transcode produces playable MP4 (or skip)
[ ] Stage 5.4: File moved to movies/Zombieland (2009)/
[ ] Stage 5.5: Temp files cleaned up
[ ] Stage 5.6-5.8: Optional stuff (verify or skip)
[ ] Stage 8: Job marked Success, duration displayed
```

## Configuration to Check Before Starting

| Setting | Where | Recommended Value |
|---------|-------|-------------------|
| `ARM_GET_VIDEO_TITLE` | appsettings.json or env | `true` |
| `ARM_MANUAL_WAIT` | appsettings.json or env | `true` (so we can verify Stage 3) |
| `ARM_SKIP_TRANSCODE` | appsettings.json or env | `true` initially (skip to test rip first) |
| `ARM_MAIN_FEATURE` | appsettings.json or env | `true` (rip only the movie) |
| `ARM_RAW_PATH` | appsettings.json or env | `/home/arm/media/raw` |
| `ARM_COMPLETED_PATH` | appsettings.json or env | `/home/arm/media` |
| `ARM_TRANSCODE_PATH` | appsettings.json or env | `/home/arm/media/transcode` |
| `ARM_LOGPATH` | appsettings.json or env | `/home/arm/logs` |
| `ARM_DBFILE` | appsettings.json or env | `/etc/arm/config/arm.db` |
| `ARM_OMDB_API_KEY` | env or appsettings | Set if CRC64 ARM API fails |
| `ARM_HB_PRESET_DVD` | appsettings.json or env | `"Super Fast 720p24"` (faster for testing) |

**Recommended first test:** Start with `SkipTranscode = true` so we only test through Stage 5.2 (rip to MKV) and Stage 5.4 (move files). This avoids the 30-90 minute transcode wait. Once we verify the front half works, enable transcode.
