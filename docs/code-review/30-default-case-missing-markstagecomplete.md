# Default switch case doesn't call `MarkStageComplete`

**Priority:** 🟢 Low
**File(s):** `src/ArmRipper.Core/Rip/Conductor.cs`
**Status:** ⬜ Todo

---

## Problem

When the disc type is unknown, the `default` case in `ProcessJobAsync` sets `Failure`
but never marks the stage complete:

```csharp
// Lines 785-789
default:
    logger.LogCritical("Couldn't identify the disc type. Exiting without any action.");
    job.Status = JobState.Failure;
    await db.SaveChangesAsync(ct);
    await BroadcastJobUpdateAsync(job);
    return 1;
```

By contrast, the `Failure` path during identification (line 628) calls
`job.MarkStageComplete(RipStage.Identify)` before returning.  If someone later
inspects `CompletedStages`, the Identify stage appears incomplete even though the
failure happened after identification finished.

## Proposed Fix

Add `job.MarkStageComplete(RipStage.Identify)` before setting `Failure`:

```csharp
default:
    logger.LogCritical("Couldn't identify the disc type. Exiting without any action.");
    job.MarkStageComplete(RipStage.Identify);  // ← add this
    job.Status = JobState.Failure;
    await db.SaveChangesAsync(ct);
    await BroadcastJobUpdateAsync(job);
    return 1;
```

## Benefits

- Consistent `CompletedStages` tracking across all failure paths
- Accurate representation that identification ran (it just couldn't determine the type)
