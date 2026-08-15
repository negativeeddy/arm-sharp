# After manual wait, `Status` is set to `Active` unconditionally

**Priority:** 🟡 Medium
**File(s):** `src/ArmRipper.Core/Rip/Conductor.cs`
**Status:** ✅ Done

---

## Problem

After the manual-wait loop exits, line 726 unconditionally overwrites the job status:

```csharp
job.Status = JobState.Active;
job.ProgressMessage = "Starting rip...";
await db.SaveChangesAsync(ct);
```

If the job was externally set to `Cancelled` or `Failure` during the wait (e.g. by
a concurrent admin action), this overwrites that terminal state.  The cancellation
check inside the loop (`if (job.Status == JobState.Cancelled) return 1;`) only
detects the `Cancelled` enum value — but the DB might have been updated to `Failure`
by another process between the last `ReloadAsync` and the status overwrite.

## Proposed Fix

Check the status after the loop and before overwriting:

```csharp
// After manual wait loop exits
await db.Entry(job).ReloadAsync(ct);
if (job.Status.IsTerminal())
{
    logger.LogWarning("Job set to terminal state {Status} during manual wait — aborting", job.Status);
    return 1;
}

job.Status = JobState.Active;
```

The `ReloadAsync` ensures we have the latest DB state before making a decision.

## Benefits

- Prevents overwriting terminal states set during the wait
- Closes a race condition between admin actions and the conductor loop
