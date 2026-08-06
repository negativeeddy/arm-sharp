# Deep-Review `Conductor.ProcessJobAsync` — Core Pipeline Orchestrator

**Priority:** 🔴 Critical
**File:** `src/ArmRipper.Core/Rip/Conductor.cs`
**Status:** ✅ Done — 8 findings produced (see #24–#31)

---

## Problem

`ProcessJobAsync` is the central orchestrator that sequences the full pipeline:
identify → rip → transcode → finalize. It likely also handles:

- **Resume-from-stage:** Picking up after a `Stopping` / `Cancelled` state
- **Manual wait loop:** Pausing for user confirmation before transcode
- **Stage transitions:** Calling `MarkStageComplete` / `TransitionToStage`
- **Error boundaries:** Deciding whether a stage failure is terminal or retryable

This method was not fully reviewed. Any bug here affects **every rip job**.

## Investigation Tasks

1. **Read the full method** and map its control flow (happy path + error paths)
2. Verify that every code path sets a terminal `JobState` (Success, Failure, Cancelled)
3. Verify resume-from-stage correctly reads `CompletedStages` and skips done phases
4. Verify the manual-wait loop respects `CancellationToken` and the `ManualWaitResume` flag
5. Check that `SaveChangesAsync` + `BroadcastJobUpdateAsync` happen at every stage transition
6. Check that the `finally` / cleanup block runs even if a stage throws

## Deliverable

After deep review, either:
- Close this task with a note: "No issues found — control flow is correct"
- Create new sub-documents for any bugs discovered

---

## Review Results (2026-08-06)

8 findings produced. See sub-documents:

| # | Finding | Priority |
|---|---------|----------|
| 24 | `ProcessJobAsync` has no resume-from-stage logic | 🔴 Critical |
| 25 | `RunAsync` always creates new job — resume path is dead | 🔴 Critical |
| 26 | Non-Active status at entry only warns, doesn't gate | 🟡 Medium |
| 27 | Synchronous `FirstOrDefault` blocks async pipeline | 🟡 Medium |
| 28 | After manual wait, Status set to Active unconditionally | 🟡 Medium |
| 29 | Duplicate `RemoveWriter` in duplicate-skip path | 🟢 Low |
| 30 | Default switch case doesn't call `MarkStageComplete` | 🟢 Low |
| 31 | `ManualWaitResume` flag not reset on timeout path | 🟢 Low |
