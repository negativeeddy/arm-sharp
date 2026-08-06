# Resume-from-stage logic is absent from `ProcessJobAsync`

**Priority:** 🔴 Critical
**File(s):** `src/ArmRipper.Core/Rip/Conductor.cs`
**Status:** ⬜ Todo

---

## Problem

`ProcessJobAsync` unconditionally calls `TransitionToStage(RipStage.Identify)` and runs
every pipeline phase (identify → rip → transcode) on every invocation.  It never checks
`job.CompletedStages` or calls `job.IsStageComplete()`.

The controller's `Resume` action explicitly sets `job.Status = Active`, checks
`job.CompletedStages`, and calls `StartRip`, expecting the conductor to skip completed
stages.  But `ProcessJobAsync` ignores those stages and re-runs everything from scratch.

```csharp
// Conductor.ProcessJobAsync — lines 618-635 (no CompletedStages check)
job.TransitionToStage(RipStage.Identify);          // ← ALWAYS transitions
await db.SaveChangesAsync(ct);
await identifyService.IdentifyAsync(job, ct);      // ← ALWAYS re-identifies
...
job.MarkStageComplete(RipStage.Identify);           // ← ALWAYS marks done
```

## Proposed Fix

Add a stage-skip loop at the top of `ProcessJobAsync` that checks `CompletedStages`
and jumps directly to the first incomplete stage:

```csharp
// Guard: skip stages already marked complete.
if (job.IsStageComplete(RipStage.Identify))
{
    logger.LogInformation("Resume: skipping Identify (already complete)");
    job.TransitionToStage(RipStage.Rip);
    goto ripDispatch;
}

// ... existing identify logic ...

ripDispatch:
switch (job.DiscType) { ... }
```

Then split the method so each stage is its own private method, making the skip logic
cleaner and the flow easier to reason about.  Combined with #25 (RunAsync new-job bug),
the resume path becomes a first-class feature.

## Benefits

- Correctly resumes jobs that were `Stopping`/`Cancelled` mid-pipeline
- Avoids re-running expensive stages (MakeMKV, HandBrake) when not needed
- Aligns with what `JobsController.Resume` already expects

## Verification

- Create a job, cancel it during transcode, resume it — it should skip identify and rip.
- Verify log output shows "Resume: skipping Identify".
