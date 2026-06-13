# ARM Sharp — Stage Entrance & Exit Criteria

This document defines formal entrance and exit criteria for each stage of the ripping pipeline. These criteria enable:
1. **Stage detection** — determining which stage a job is currently at (critical for resume)
2. **Validation** — verifying prerequisites before a stage executes
3. **Resume** — checking if a partially completed job can resume from a specific stage
4. **Error recovery** — identifying which stage failed and whether retry is safe

---

## Stage Enum Proposal

A formal `StageName` enum (replacing the current ad-hoc `Job.Stage` timestamp field) would look like:

```csharp
public enum StageName
{
    Uninitialized,      // Before any processing
    Setup,              // 1
    Identify,           // 2
    ManualOverride,     // 3 (optional)
    Dispatch,           // 4
    // Video sub-stages:
    TrackIdentification,// 5.1
    RipWithMakeMkv,     // 5.2
    Transcode,          // 5.3
    MoveFiles,          // 5.4
    Cleanup,            // 5.5
    EmbyRefresh,        // 5.6 (optional)
    Permissions,        // 5.7
    Notify,             // 5.8
    // Special types:
    RipAudio,           // 6
    RipData,            // 7
    Finalize            // 8
}
```

---

## Stage 1: SETUP

### Purpose
Create the job record, ensure all directories exist, snapshot configuration at rip time.

### Entrance Criteria
| # | Criterion | Check |
|---|-----------|-------|
| 1.1 | `job.Id > 0` | Job record exists in DB |
| 1.2 | `job.DevPath` is non-null and non-empty | Device path is known |
| 1.3 | `job.Status == JobState.Active` | Job is in active state |
| 1.4 | `job.DiscType == DiscType.Unknown` | Disc type hasn't been determined yet |
| 1.5 | `job.Config == null` | Config snapshot not yet created |

### Exit Criteria
| # | Criterion | Check |
|---|-----------|-------|
| 1.1 | `job.Id > 0` | Job persisted |
| 1.2 | `job.Config != null` and `job.Config.JobId == job.Id` | Config snapshot created |
| 1.3 | `job.LogFile` is non-null | Log file path assigned |
| 1.4 | `job.StartTime != default` | Start timestamp recorded |
| 1.5 | Directories exist: `RawPath`, `TranscodePath`, `CompletedPath`, `LogPath` | All required directories created |
| 1.6 | `job.Status == JobState.Active` | Status unchanged (still active) |

### Side Effects (what gets written to DB)
- `Jobs` row inserted (assigned `job.Id`)
- `ConfigSnapshots` row inserted (referencing `job.Id`)
- No filesystem changes except directory creation

### Resume / Re-entry
- **Safe to re-enter?** No — would create duplicate job.
- **Can skip?** If `job.Id > 0 && job.Config != null`, skip entirely.

---

## Stage 2: IDENTIFY DISC

### Purpose
Mount the disc (if needed), detect disc type, extract label, fetch metadata from online APIs, compute disc fingerprint, handle duplicates.

### Entrance Criteria
| # | Criterion | Check |
|---|-----------|-------|
| 2.1 | `job.Id > 0` | Job exists |
| 2.2 | `job.DevPath` is non-null and non-empty | Device path known |
| 2.3 | `job.Status == JobState.Active` or `JobState.VideoInfo` | Job is in appropriate state |
| 2.4 | `job.DiscType == DiscType.Unknown` | Disc type not yet identified |
| 2.5 | Device at `job.DevPath` exists (block device or ISO) | `/dev/sr0` exists or ISO file present |

### Exit Criteria (success)
| # | Criterion | Check |
|---|-----------|-------|
| 2.1 | `job.DiscType != DiscType.Unknown` | Disc type determined (Dvd, Bluray, Music, or Data) |
| 2.2 | `job.Label` is non-null | Disc label extracted |
| 2.3 | `job.DiscFingerprint` is non-null | Fingerprint computed: `{Label}::{SectorCount}` |
| 2.4 | `job.Status == JobState.Active` | Status set back to active |
| 2.5 | `job.Errors` is null or empty | No unrecoverable errors |

### Exit Criteria (success — video specific)
| # | Criterion | Check |
|---|-----------|-------|
| 2.6 | `job.CrcId` is set (DVD) or non-null | CRC64 computed (DVD only; BD may be null) |
| 2.7 | `job.Title` is non-null | Title determined (auto or label fallback) |
| 2.8 | `job.Year` is set (may be "0000" if unknown) | Year from API or placeholder |
| 2.9 | `job.VideoType` is set ("movie", "series", or null) | Video type from API |
| 2.10 | `job.HasNiceTitle` indicates whether metadata was found | Flag for title quality |

### Exit Criteria (failure)
| # | Criterion | Check |
|---|-----------|-------|
| 2.F1 | `job.DiscType == DiscType.Unknown` | Default dispatch will fail with critical error |
| 2.F2 | `job.Status == JobState.Failure` | Only if mount failed unrecoverably |
| 2.F3 | `job.Errors` describes the failure | Error message set |

### Side Effects
- `job.MountPoint` set (if disc mounted)
- `job.DiscType` set
- `job.Label` set
- If video: `job.CrcId`, `job.Title`, `job.TitleAuto`, `job.Year`, `job.YearAuto`, `job.VideoType`, `job.VideoTypeAuto`, `job.PosterUrl`, `job.PosterUrlAuto`, `job.ImdbId`, `job.ImdbIdAuto`, `job.HasNiceTitle` set
- `job.DiscFingerprint` set
- DB row updated (all above fields persisted)

### Resume / Re-entry
- **Safe to re-enter?** No — mounting again is safe but API calls would be wasteful.
- **Can skip?** If `job.DiscType != DiscType.Unknown` and `job.DiscFingerprint != null`, skip.
- **Resume detection:** Check `job.DiscType != DiscType.Unknown`.

---

## Stage 3: MANUAL OVERRIDE (Optional)

### Purpose
Wait for user to manually override the auto-detected title before ripping begins.

### Precondition (this stage is skipped entirely if not configured)
`job.Config.ManualWait == true`

### Entrance Criteria
| # | Criterion | Check |
|---|-----------|-------|
| 3.1 | `job.DiscType != DiscType.Unknown` | Disc identified |
| 3.2 | `job.Status == JobState.Active` | From previous stage |
| 3.3 | `job.Config.ManualWait == true` | Manual wait is enabled |
| 3.4 | `string.IsNullOrEmpty(job.TitleManual)` | No manual override yet entered |
| 3.5 | `!string.IsNullOrEmpty(job.Label)` | Label exists for user context |

### Exit Criteria (manual override provided)
| # | Criterion | Check |
|---|-----------|-------|
| 3.1 | `!string.IsNullOrEmpty(job.TitleManual)` | User entered a title override |
| 3.2 | `job.Status == JobState.Active` | Status reset to active |

### Exit Criteria (timeout)
| # | Criterion | Check |
|---|-----------|-------|
| 3.3 | `job.Status == JobState.Active` | Status reset to active |
| 3.4 | Warnings may include "Manual wait expired" | Warning logged |
| 3.5 | `job.TitleManual` still empty (or could have been set before timeout) | No override |

### Exit Criteria (cancelled)
| # | Criterion | Check |
|---|-----------|-------|
| 3.6 | `job.Status == JobState.Cancelled` | Job cancelled by user |
| 3.7 | Conductor returns exit code 1 | Pipeline aborted |

### Side Effects
- `job.Status = ManualWaitStarted` → polling loop → `job.Status = Active`
- If cancelled: `job.Status = Cancelled`

### Resume / Re-entry
- **Safe to re-enter?** Yes — wait loop is idempotent.
- **Can skip?** If `job.TitleManual` is already set, skip wait.
- **Resume detection:** Check `job.Status` — if `ManualWaitStarted`, the job was mid-wait.

---

## Stage 4: DISPATCH

### Purpose
Route to the correct ripping pipeline based on disc type.

### Entrance Criteria
| # | Criterion | Check |
|---|-----------|-------|
| 4.1 | `job.DiscType != DiscType.Unknown` | Disc was identified |
| 4.2 | `job.Status == JobState.Active` | Job in active state |
| 4.3 | `job.Label` non-null (may be "not identified") | Label present |
| 4.4 | `job.Config != null` | Config snapshot exists |

### Exit Criteria
| # | Criterion | Check |
|---|-----------|-------|
| 4.1 | One of: `DiscType.Dvd`, `DiscType.Bluray`, `DiscType.Music`, `DiscType.Data` | Disc type is routable |
| 4.2 | If video: `ArmRipperService.RipVisualMediaAsync()` has been called | Pipeline continues to Stage 5 |
| 4.3 | If music: `RipMusicAsync()` completed OR entered Stage 6 | |
| 4.4 | If data: `RipDataAsync()` completed OR entered Stage 7 | |
| 4.5 | If unknown: `job.Status == JobState.Failure` with error message | Fail gracefully |

### Side Effects
- No direct DB writes in dispatch itself
- Notification sent: `NotifyEntryAsync()`

### Resume / Re-entry
- **Safe to re-enter?** No — would re-send notification and re-dispatch.
- **Can skip?** If `job.DiscType` is known and routing has happened (check `job.Path` or `job.Tracks.Any()`), skip.
- **Resume detection:** Check `job.Status` — if it's past Dispatch and not `Active`, infer from disc type.

---

## Stage 5: VIDEO RIP PIPELINE (DVD/Blu-ray)

This is a composite stage with multiple sub-stages.

### Sub-stage 5.1: TRACK IDENTIFICATION

#### Purpose
Query disc structure to identify available tracks, filter by length, identify main feature.

#### Entrance Criteria
| # | Criterion | Check |
|---|-----------|-------|
| 5.1.1 | `job.DiscType == DiscType.Dvd` or `DiscType.Bluray` | Disc is video |
| 5.1.2 | `job.Status == JobState.Active` or `JobState.VideoInfo` | From identify stage |
| 5.1.3 | `job.DiscFingerprint` is non-null | Fingerprint computed |
| 5.1.4 | Device is accessible (mounted or via MakeMKV) | `job.MountPoint` or raw device |

#### Exit Criteria (success — tracks found)
| # | Criterion | Check |
|---|-----------|-------|
| 5.1.5 | `job.Status == JobState.VideoRipping` | Status advanced to rip |
| 5.1.6 | `job.Tracks.Count > 0` | At least one track identified (may be 0 for encrypted BD → fallback) |
| 5.1.7 | At least one track has `Track.Process == true` OR fallback `RipAllTitles` will run | Eligible tracks or fallback |
| 5.1.8 | The longest eligible track has `Track.MainFeature == true` | Main feature identified |
| 5.1.9 | `job.NoOfTitles` reflects track count (if known) | Count saved |

#### Exit Criteria (success — no tracks = encrypted BD fallback)
| # | Criterion | Check |
|---|-----------|-------|
| 5.1.10 | `job.Tracks.Count == 0` | No tracks from makemkvcon |
| 5.1.11 | `job.DiscType == DiscType.Bluray` or encrypted DVD | Likely copy protection |
| 5.1.12 | Fallback: `makeMkv.RipAllTitlesAsync()` will run | Rip strategy switches |
| 5.1.13 | `job.Status == JobState.VideoRipping` | Status advanced |

#### Exit Criteria (failure)
| # | Criterion | Check |
|---|-----------|-------|
| 5.1.14 | `job.Status == JobState.Failure` | Fatal error |
| 5.1.15 | `job.Errors` describes failure | Error message set |

#### Side Effects
- `Tracks` rows inserted into database (with `JobId`)
- If cached: tracks loaded from `disc_metadata`, no new MakeMKV call
- `job.Status = VideoInfo` (briefly) → `VideoRipping`
- `job.NoOfTitles` set

#### Resume / Re-entry
- **Safe to re-enter?** No — would re-run makemkvcon and duplicate tracks.
- **Can skip?** If `job.Tracks.Any(t => t.Process)` or `job.Status >= VideoRipping`, skip.
- **Resume detection:** Check `job.Tracks.Count > 0` or `job.Status == VideoRipping`.

---

### Sub-stage 5.2: RIP WITH MAKEMKV

#### Purpose
Extract video tracks from disc using MakeMKV.

#### Entrance Criteria
| # | Criterion | Check |
|---|-----------|-------|
| 5.2.1 | `job.Status == JobState.VideoRipping` | Track identification complete |
| 5.2.2 | `job.DiscType == DiscType.Dvd` or `DiscType.Bluray` | Video disc |
| 5.2.3 | Either: `job.Tracks.Any(t => t.Process)` OR `job.Tracks.Count == 0` (fallback) | Eligible tracks or fallback |
| 5.2.4 | Output directory `{RawPath}/{JobTitle}` exists or will be created | Write target ready |
| 5.2.5 | Config snapshot accessible: `job.Config` or `settings.Value` | Config for rip strategy |

#### Exit Criteria (success)
| # | Criterion | Check |
|---|-----------|-------|
| 5.2.6 | MKV files exist in `{RawPath}/{JobTitle}/` | At least one `.mkv` file produced |
| 5.2.7 | For each track where `Track.Process == true`: `Track.Ripped == true` and `Track.FileName` is non-null | Tracks marked as ripped |
| 5.2.8 | For each ripped track: `Track.FileSize` is set (non-null, non-zero) | File size recorded |
| 5.2.9 | `job.Status == JobState.VideoRipping` | Status unchanged (still ripping) |
| 5.2.10 | `job.MakeMkvProgress >= 100` or null | Progress completed or unset |

#### Exit Criteria (failure)
| # | Criterion | Check |
|---|-----------|-------|
| 5.2.11 | `job.Status == JobState.Failure` | Fatal error |
| 5.2.12 | `job.Errors` describes the failure (e.g., "MakeMKV rip produced no output") | Error message set |
| 5.2.13 | Output directory may contain partial files | Partial output for debugging |

#### Side Effects
- MKV files written to `{RawPath}/{JobTitle}/title_*.mkv`
- `Track.Ripped`, `Track.FileName`, `Track.FileSize`, `Track.Source = "MakeMKV"` updated
- `job.MakeMkvProgress` updated (0-100)
- `job.ProgressMessage` updated
- DB: Tracks updated (ripped flag, file info)

#### Resume / Re-entry
- **Safe to re-enter?** No — would re-rip producing duplicate files (or overwrite). Some files may already exist.
- **Can skip?** If all eligible tracks have `Track.Ripped == true` AND output files exist, skip.
- **Resume detection:** Check `job.Tracks.All(t => !t.Process || t.Ripped)` — if all processed, stage is done.
- **Partial resume:** If some tracks have `Track.Ripped == true` but not all, could skip completed tracks. NOT currently implemented.

---

### Sub-stage 5.3: TRANSCODE

#### Purpose
Re-encode ripped MKV files to target format (H.264/H.265) using HandBrake or FFmpeg.

#### Entrance Criteria
| # | Criterion | Check |
|---|-----------|-------|
| 5.3.1 | `job.Status == JobState.VideoRipping` | Rip completed |
| 5.3.2 | MKV files exist in `{RawPath}/{JobTitle}/` OR transcode input path exists | Input files present |
| 5.3.3 | `job.Config.SkipTranscode == false` (or default) | Transcode not disabled |
| 5.3.4 | `job.Tracks.Any(t => t.Ripped)` | At least one ripped track |

#### Exit Criteria (success)
| # | Criterion | Check |
|---|-----------|-------|
| 5.3.5 | Output files exist in `{TranscodePath}/{TypeSubFolder}/{JobTitle}/` | Encoded files produced |
| 5.3.6 | `job.Status == JobState.Active` (not Failure) | Status reset after transcode |
| 5.3.7 | `job.TranscodeProgress >= 100` or null | Progress completed or unset |
| 5.3.8 | `job.Tracks.All(t => !t.Ripped || string.IsNullOrEmpty(t.Error))` | No track-level errors (or all errors handled) |

#### Exit Criteria (skip)
| # | Criterion | Check |
|---|-----------|-------|
| 5.3.9 | `job.Config.SkipTranscode == true` | Transcode disabled |
| 5.3.10 | `job.Status == JobState.Active` (if no error) or `VideoRipping` (unchanged) | Status remains appropriate |

#### Exit Criteria (failure)
| # | Criterion | Check |
|---|-----------|-------|
| 5.3.11 | `job.Status == JobState.Failure` | Fatal error |
| 5.3.12 | `job.Errors` describes the failure | Error message set |
| 5.3.13 | Partial output in transcode directory (for debugging) | Intermediate files retained |
| 5.3.14 | `Track.Error` set for individual failed tracks | Per-track error |

#### Side Effects
- Encoded files written to `{TranscodePath}/{TypeSubFolder}/{JobTitle}/`
- Raw input files left in place (cleaned up later in Stage 5.5)
- `job.Status = TranscodeActive` → `Active` (success) or `Failure`
- `job.TranscodeProgress` updated
- `job.ProgressMessage` updated
- If notify on rip complete: notification sent before transcode starts

#### Resume / Re-entry
- **Safe to re-enter?** No — would re-transcode existing files. Output files would be overwritten.
- **Can skip?** If output files exist in transcode path, skip.
- **Resume detection:** Difficult without per-track transcode tracking. Check if transcode output directory exists and has files.

---

### Sub-stage 5.4: MOVE FILES TO FINAL LOCATION

#### Purpose
Move transcoded (or raw, if skip transcode) files from transcode path to final completed path with proper naming.

#### Entrance Criteria
| # | Criterion | Check |
|---|-----------|-------|
| 5.4.1 | `job.Status == JobState.Active` | Transcode completed (or skipped) |
| 5.4.2 | Files exist in source path: either `{TranscodePath}/...` or `{RawPath}/...` | Files to move exist |
| 5.4.3 | `job.Tracks.Any(t => t.Ripped)` | Tracks to move |
| 5.4.4 | `job.VideoType` is known ("movie", "series", or null) | Determines move strategy |
| 5.4.5 | `job.Path` (final directory) is set | Mapped output directory known |

#### Exit Criteria (success)
| # | Criterion | Check |
|---|-----------|-------|
| 5.4.6 | Files exist in `job.Path` (or extras sub-folder) | Files at final destination |
| 5.4.7 | Files no longer exist in source path (moved, not copied) | Cleanup applies |
| 5.4.8 | `Track.FileName` updated to final filename (if renamed) | Track record updated |
| 5.4.9 | `job.Status == JobState.Active` | Status unchanged |

#### Exit Criteria (failure)
| # | Criterion | Check |
|---|-----------|-------|
| 5.4.10 | `job.Status == JobState.Failure` | Move failed |
| 5.4.11 | Files may be in source path (not cleaned up) | Debugging possible |

#### Side Effects
- Files moved from transcode path → final path
- Directories created under `job.Path` (extras sub-folder)
- `Track.NewFileName` may be set if renamed
- Source directories may be emptied

#### Resume / Re-entry
- **Safe to re-enter?** Not safely — files have already been moved. `MoveFileMain()` guards with `if (File.Exists(newFile)) return;` so overwrite is prevented.
- **Can skip?** If `job.Path` directory exists and has files, skip.
- **Resume detection:** Check `Directory.Exists(job.Path) && Directory.EnumerateFileSystemEntries(job.Path).Any()`.

---

### Sub-stage 5.5: CLEANUP RAW/TRANSCODE DIRECTORIES

#### Purpose
Delete intermediate raw and transcode directories to free disk space.

#### Entrance Criteria
| # | Criterion | Check |
|---|-----------|-------|
| 5.5.1 | `job.Status == JobState.Active` | Move completed |
| 5.5.2 | Source directories exist (raw path, transcode path) | Directories to delete |
| 5.5.3 | (If skip transcode + rip with Mkv): transcode path == raw path, delete transcode path only | Careful with alias paths |

#### Exit Criteria
| # | Criterion | Check |
|---|-----------|-------|
| 5.5.4 | Raw directory deleted or emptied | Disk space freed |
| 5.5.5 | Transcode directory deleted or emptied | No intermediate files remain |
| 5.5.6 | Errors during delete are non-fatal (logged as debug) | Job continues regardless |
| 5.5.7 | `job.Status == JobState.Active` | Status unchanged |

#### Side Effects
- Directories removed from filesystem
- Non-critical errors logged but ignored

#### Resume / Re-entry
- **Safe to re-enter?** Yes — deleting already-deleted directories is a no-op.
- **Can skip?** If directories don't exist, skip.
- **Resume detection:** Check `!Directory.Exists(rawPath) && !Directory.Exists(transcodePath)`.

---

### Sub-stage 5.6: EMBY LIBRARY REFRESH (Optional)

#### Purpose
Trigger Emby/Jellyfin to scan the new files.

#### Entrance Criteria
| # | Criterion | Check |
|---|-----------|-------|
| 5.6.1 | `job.Config.EmbyRefresh == true` | Emby refresh enabled |
| 5.6.2 | `job.Config.EmbyServer` is non-null and non-empty | Server configured |
| 5.6.3 | Files exist in `job.Path` | Final files ready |

#### Exit Criteria
| # | Criterion | Check |
|---|-----------|-------|
| 5.6.4 | HTTP POST to Emby Library/Refresh returned 2xx | API call succeeded |
| 5.6.5 | OR error is logged (non-fatal) | Failure doesn't stop job |
| 5.6.6 | `job.Status == JobState.Active` | Status unchanged |

#### Resume / Re-entry
- **Safe to re-enter?** Yes — idempotent API call.
- **Can skip?** If already called (no tracking field), can't detect. Safe to call again.

---

### Sub-stage 5.7: SET PERMISSIONS

#### Purpose
Set `chmod 777` on final output directory for broad access.

#### Entrance Criteria
| # | Criterion | Check |
|---|-----------|-------|
| 5.7.1 | `job.Path` is non-null and exists | Final directory exists |
| 5.7.2 | `job.Config` is non-null | Config for permissions check |
| 5.7.3 | `job.Status == JobState.Active` | Not failed |

#### Exit Criteria
| # | Criterion | Check |
|---|-----------|-------|
| 5.7.4 | Permissions set recursively on `job.Path` | `chmod 777` applied |
| 5.7.5 | Errors are non-fatal (logged) | Failure doesn't stop job |
| 5.7.6 | `job.Status == JobState.Active` | Status unchanged |

#### Resume / Re-entry
- **Safe to re-enter?** Yes — idempotent.
- **Can skip?** If already set, safe to skip. No tracking field.

---

### Sub-stage 5.8: NOTIFY COMPLETION

#### Purpose
Send success/failure notification via configured channels (Apprise, Pushbullet, etc.).

#### Entrance Criteria
| # | Criterion | Check |
|---|-----------|-------|
| 5.8.1 | `job.Config.NotifyTranscode == true` | Notifications enabled |
| 5.8.2 | At least one notification channel configured (Apprise, PbKey, IftttKey, etc.) | Channel to send to |
| 5.8.3 | `job.Status == JobState.Active` or `JobState.Failure` | Job finished (success or fail) |

#### Exit Criteria
| # | Criterion | Check |
|---|-----------|-------|
| 5.8.4 | Notification sent (or attempted) | Best-effort delivery |
| 5.8.5 | Errors are non-fatal (logged) | Failure doesn't stop job |
| 5.8.6 | `job.Status == JobState.Active` (success) or `JobState.Failure` | Status unchanged |

#### Resume / Re-entry
- **Safe to re-enter?** Yes — idempotent (user may get duplicate notifications).
- **Can skip?** If already notified (no tracking field), can't detect. Safe to re-notify.

---

## Stage 6: AUDIO CD RIP

### Purpose
Rip audio CD tracks using abcde, with MusicBrainz lookup for metadata.

### Entrance Criteria
| # | Criterion | Check |
|---|-----------|-------|
| 6.1 | `job.DiscType == DiscType.Music` | Audio CD detected |
| 6.2 | `job.Status == JobState.Active` | From dispatch |
| 6.3 | `abcde` command available on PATH | `which abcde` succeeds (or `abcde.conf` exists) |
| 6.4 | Device accessible: `job.DevPath` | Can read audio CD |

### Exit Criteria (success)
| # | Criterion | Check |
|---|-----------|-------|
| 6.5 | `job.Status == JobState.Active` | After rip, before finalize |
| 6.6 | No `job.Errors` set | No errors |
| 6.7 | abcde completed with exit code 0 | Process succeeded |

### Exit Criteria (failure)
| # | Criterion | Check |
|---|-----------|-------|
| 6.8 | `job.Status == JobState.Failure` | Fatal error |
| 6.9 | `job.Errors` describes failure (e.g., "Call to abcde failed") | Error message set |

### Side Effects
- `job.Status = AudioRipping` → `Active` or `Failure`
- abcde output written to log file
- Audio files created in abcde default output location (NOT tracked in Job model)

### Resume / Re-entry
- **Safe to re-enter?** No — abcde would re-rip and potentially duplicate files.
- **Can skip?** No reliable detection. If job already succeeded, finalize handles it.

---

## Stage 7: DATA DISC RIP

### Purpose
Rip data disc to ISO using dd.

### Entrance Criteria
| # | Criterion | Check |
|---|-----------|-------|
| 7.1 | `job.DiscType == DiscType.Data` | Data disc detected |
| 7.2 | `job.Status == JobState.Active` | From dispatch |
| 7.3 | Output path writable: `{RawPath}/{Label}/` can be created | Write access |
| 7.4 | Device accessible: `job.DevPath` | Can read disc |

### Exit Criteria (success)
| # | Criterion | Check |
|---|-----------|-------|
| 7.5 | `{CompletedPath}/data/{Label}/{Label}.iso` exists | ISO file at final location |
| 7.6 | `job.Status == JobState.Active` | After rip |
| 7.7 | No `job.Errors` set | No errors |

### Exit Criteria (failure)
| # | Criterion | Check |
|---|-----------|-------|
| 7.8 | `job.Status == JobState.Failure` | Fatal error |
| 7.9 | `job.Errors` describes failure (e.g., "Data rip failed") | Error message set |
| 7.10 | Temporary `.part` file deleted on failure | Cleanup attempted |

### Side Effects
- `.part` file at `{RawPath}/{Label}/{Label}.part` → moved to `{CompletedPath}/data/{Label}/{Label}.iso`
- Intermediate directory deleted after move
- No intermediate JobState set (remains `Active` throughout)

### Resume / Re-entry
- **Safe to re-enter?** No — would re-rip overwriting existing files.
- **Can skip?** If `{CompletedPath}/data/{Label}/{Label}.iso` exists, skip.

---

## Stage 8: FINALIZE

### Purpose
Set final job status, calculate duration, log completion.

### Entrance Criteria
| # | Criterion | Check |
|---|-----------|-------|
| 8.1 | `job.DiscType` set and routing completed | Pipeline finished |
| 8.2 | `job.Status` is `Active` (success) or `Failure` (error) | Job outcome known |
| 8.3 | (For video) `job.Path` set and verified (or failed) | Output path validated |

### Exit Criteria (success)
| # | Criterion | Check |
|---|-----------|-------|
| 8.4 | `job.Status == JobState.Success` | Terminal success state |
| 8.5 | `job.StopTime != default` | End timestamp recorded |
| 8.6 | `job.JobLength` formatted as `h:mm:ss` | Duration computed |
| 8.7 | (For video) Files exist in `job.Path` and directory is non-empty | Output verified |

### Exit Criteria (failure)
| # | Criterion | Check |
|---|-----------|-------|
| 8.8 | `job.Status == JobState.Failure` | Terminal failure state |
| 8.9 | `job.Errors` describes the failure (or "Job completed but no output files found") | Error message set |
| 8.10 | `job.StopTime != default` | End timestamp recorded |

### Side Effects
- `job.Status` set to `Success` or `Failure`
- `job.StopTime` set
- `job.JobLength` computed
- Log message: "ARM processing complete"

### Resume / Re-entry
- **Safe to re-enter?** Yes — idempotent (already terminal).
- **Can skip?** If `job.Status.IsTerminal()` — already done.
- **Resume detection:** `job.Status == Success` or `Failure` or `Cancelled`.

---

## Quick Reference: Stage Detection Matrix

### How to detect current stage from DB state

| Stage | Primary Indicator | Secondary Checks |
|-------|------------------|------------------|
| **Uninitialized** | `job.Id == 0` | — |
| **Setup** | `job.Config == null` | `job.LogFile == null` |
| **Identify** | `job.DiscType == Unknown` AND `job.Id > 0` AND Config exists | `job.MountPoint == null` |
| **Identify (video meta)** | `job.DiscType != Unknown` AND `job.Title == null` | `job.CrcId` being computed |
| **Manual Override** | `job.Status == ManualWaitStarted` | `string.IsNullOrEmpty(job.TitleManual)` |
| **Track Identification** | `job.Status == VideoInfo` or `VideoRipping` AND `job.Tracks.Count == 0` | `job.DiscType is Dvd or Bluray` |
| **Rip With MakeMkv** | `job.Status == VideoRipping` AND `job.Tracks.Any()` | Partial `Track.Ripped` |
| **Transcode** | `job.Status == TranscodeActive` | Previous stage complete |
| **Move Files** | `job.Status == Active` AND `Directory.Exists(job.Path)` | Source files present, dest empty |
| **Cleanup** | `job.Status == Active` AND source dirs exist | Raw/transcode paths exist |
| **Emby Refresh** | No detection field (needs one) | Config says enabled |
| **Finalize** | `job.Status` is already terminal | `StopTime` set |

### Stage Transitions: What changes

| Transition | Field Changes |
|-----------|--------------|
| Setup → Identify | `job.Config` non-null, dirs created |
| Identify → Manual Override | `job.DiscType` set, label extracted |
| Identify → Dispatch | `job.DiscType` set, fingerprint computed |
| Dispatch → Track Identification | job.Status = `VideoInfo`/`VideoRipping` |
| Track ID → Rip With MakeMkv | `job.Tracks.Count > 0`, status = `VideoRipping` |
| Rip → Transcode | `Track.Ripped == true` for eligible tracks |
| Transcode → Move | Status = `Active`, transcode output exists |
| Move → Cleanup | Files in `job.Path`, source dirs emptied |
| Any → Finalize | Pipeline complete (or failed) |

---

## Implementation Recommendations

### 1. Add `StageName` enum and `Job.CurrentStage` field
Replace the misused `Job.Stage` (currently a timestamp) with a proper stage tracker.

### 2. Add `Job.StageEnteredAt` timestamp
Track when each stage was entered, for progress/time estimation.

### 3. Add `Job.RecoveryStage` (nullable)
If a job fails, record which stage it should resume from after user intervention.

### 4. Formalize guard methods
The existing `GuardStage()` checks `job.Status` — extend to check all entrance criteria before proceeding.

### 5. Add stage transition logging
Log entrance/exit for each stage with criteria check results, for debugging resume issues.

### 6. Implement `CanResumeFrom(StageName)` function
```csharp
public static bool CanResumeFrom(Job job, StageName stage)
{
    return stage switch
    {
        StageName.Setup => job.Id == 0 || job.Config == null,
        StageName.Identify => job.DiscType == DiscType.Unknown && job.Config != null,
        StageName.TrackIdentification => job.DiscType is DiscType.Dvd or DiscType.Bluray && !job.Tracks.Any(),
        StageName.RipWithMakeMkv => job.Tracks.Any(t => t.Process && !t.Ripped),
        StageName.Transcode => job.Tracks.Any(t => t.Ripped) && !TranscodeOutputExists(job),
        StageName.MoveFiles => TranscodeOutputExists(job) && !FinalOutputExists(job),
        StageName.Cleanup => FinalOutputExists(job) && SourceDirsExist(job),
        StageName.Finalize => !job.Status.IsTerminal(),
        _ => false
    };
}
```
