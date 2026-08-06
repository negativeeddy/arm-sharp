# Deep-Review `Conductor.ProcessJobAsync` — Core Pipeline Orchestrator

**Priority:** 🔴 Critical
**File:** `src/ArmRipper.Core/Rip/Conductor.cs`
**Status:** ⬜ Todo

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
