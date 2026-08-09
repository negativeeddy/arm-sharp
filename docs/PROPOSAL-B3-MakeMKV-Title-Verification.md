# B3 — MakeMKV Title Verification (Post-Rip Output Validation) — Proposal

> **Status:** Proposed — not implemented
> **Last updated:** 2026-08-09
> **Related work:** `fix/makemkv-rip-validation` (B1 message capture + B2 undersize gate) — implemented

## 1. Problem

Job 977 (The Devil Wears Prada, DVD) was marked **Success** but produced the **wrong main feature**.

Relevant sequence from the job log:

| Time | Message | Meaning |
|------|---------|---------|
| 10:51:31 | `GetTrackInfo: ... 1 tracks, lastTid=0` | info scan (minlength=900) found the 1:49:15 feature as TINFO index `0` |
| 10:51:38 | `mkv --minlength=0 dev:/dev/sr1 0 "…/raw/…"` | rip targets TINFO index `0` |
| 10:52:31 | `MSG:2003 … UNRECOVERED READ ERROR` | damaged sector read |
| 10:53:09 | `MSG:3015 "Title #1 (1:49:15) was skipped due to navigation error"` | **the feature is dropped** |
| 10:53:10 | `MSG:3028 "Title #2 was added (1 cell(s), 0:00:09)"` | a 9-second clip is saved as `title_t00.mkv` |
| — | file→track matching: `t00` → track 0, `FileSize` overwritten, `Ripped=true` | the 9s file is accepted as the main feature |
| — | transcode runs, job → **Success** | the wrong file ships |

Root cause: the TINFO index used for the `mkv` command is **not** MakeMKV's internal title number. When the real feature (internal title #1) is skipped mid-rip, MakeMKV slides the next salvaged title (internal title #2, the 9s clip) into the output filename for TINFO index 0 (`title_t00.mkv`). Nothing downstream notices: exit code is 0, files are present, and the "N titles saved" count is consistent with expectation.

## 2. Why the naive fixes fail

- **Rip with the same `--minlength` as the info scan.** Reintroduces the exit-code-12 bug (MakeMKV skips a title that no longer meets `--minlength` once identified, then fails the whole pass). This exact fix was tried and reverted — see §3.
- **Trust the "Saving N titles" count.** The count was correct in job 977; the wrong *title* was saved, not the wrong *number* of titles.
- **Match on TINFO index.** The index is exactly what is unreliable here. MakeMKV's `PRGV` progress lines and `MSG:3015/3028` messages reference its internal title numbers, which shift when titles are skipped.

## 3. Relevant history

- `2f77744` → `4a00e1d` → `b07ff2c`: used `SourceTitleId` (TINFO field 24) to select the title for `mkv`. It caused **wrong-track rips on every DVD** and was removed (`b07ff2c` "Remove SourceTitleId: it caused wrong-track rips on every DVD"). `TrackNumber` remains the 0-based TINFO index.
- `4542288` / `517a94b` / `91fd3a6`: "omit `--minlength` when ripping a known track" → reverted (`91fd3a6`) because MakeMKV failed the pass (exit code 12).
- `83bdf63`: current behavior — single-track rip passes `minLength=0` once the track is already identified, so MakeMKV won't filter it out.
- DiscDb scan uses `minlength=0` (`ArmRipperService.cs:243`) so short extras are discoverable/promotable; the rip itself still uses the configured `minLengthCfg` (or `0` for DiscDb-promoted tracks).

Conclusion: **selection-time** signals cannot be trusted on damaged discs. Verification must happen against the **output** itself.

## 4. Options

### Option A — Probe the output with ffprobe (Recommended)

After the rip, run `ffprobe` on each output file that was matched to a track and compare **duration** (and size) against the expected track (`TINFO` Duration / FileSize):

- Robust — depends only on the file MakeMKV actually wrote, never on MakeMKV's internal numbering or message wording.
- Complements B2's size gate (already landed): B2 catches gross undersize cheaply; A adds a duration check that also catches the wrong-title case if sizes happen to coincide.
- Cheap — one `ffprobe` per output file (<1 s each); an `FfmpegService` wrapper already exists.
- Tolerance must be generous because damaged discs legitimately trim cells (`MSG:3037/3038` cell removal was present in job 977 too) and PAL/NTSC rounding shifts durations slightly. Suggested starting bound: fail if the main-feature output duration is `< 50%` of expected, or `< minLength`.

### Option B — Correlate mkv-phase TINFO/PRG messages

Track MakeMKV's internal title numbering during the rip phase and map it to our TINFO index. Fragile: numbering shifts mid-rip, is version-dependent, and is exactly the mechanism that deceived us. **Not recommended.**

### Option C — Rip all eligible titles, then select the main feature post-hoc

Always rip all eligible titles (respecting `minLength`), then pick the longest/fullest as the main feature by examining output files. Completely removes dependence on index mapping — the mapping never matters because everything is ripped.

- Cost: more rip time and disk; interacts with DiscDb-promoted extras (deliberate selections) and `MainFeature` mode.
- Best treated as a future, optional mode ("rip all + pick longest") rather than a default behavior change.

### Option D — Fail fast when a skipped-title MSG references the selected title

B1 already captures `MSG:3015/3025/2003/4004`. A refinement would fail immediately if the *selected* target was skipped. Blocked by the same mapping problem: `MSG:3015` names MakeMKV's internal title number (`Title #1`), which does not equal the TINFO index we selected (`0`). D alone cannot reliably identify "our title was skipped"; it is useful only as a warning and as corroboration for A.

## 5. Recommendation

Implement **A** on top of the landed B1/B2 work:

1. Keep B2's size gate as the cheap first line of defense (fails before transcode, raw files retained for retry).
2. Add an ffprobe **duration** check as the authoritative verification of the main-feature track (and optionally other rip targets).
3. On failure, mirror the existing failure path: `JobState.Failure`, `job.Errors`, log, throw — transcode is skipped and raw files are kept.
4. Non-target (extra) undersized/truncated outputs → warn only, do not fail the job.

## 6. Rollout / testing

- **Unit:** a pure verdict helper (`expected duration/size vs actual + tolerance → pass/warn/fail`) tested via the existing reflection-based `ArmRipperServiceLogicTests` pattern.
- **Integration:** `FfmpegService` probe against fixture .mkv files; simulate the job-977 scenario (stubbed rip output + captured MakeMKV messages) to assert the job fails at rip instead of marking Success.
- **E2E:** run a real damaged disc and confirm the job fails at the rip stage and can be retried.
- **Watch for false-positives:** cell-trimmed titles (`MSG:3037/3038`), PAL/NTSC duration rounding, and multi-title discs where the main feature is deliberately not the largest output.

## 7. Open questions

- Tolerance values: percentage of expected duration vs absolute seconds vs `minLength` floor?
- Should verification apply only to the MainFeature track, or to all rip targets (non-main → warn)?
- Should "verify output" become a visible `RipStage` step for UI status?
- Is `ffprobe` guaranteed present in the Docker image alongside `makemkvcon`/`ffmpeg`?
