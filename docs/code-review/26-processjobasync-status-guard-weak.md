# Non-Active status at entry only warns, doesn't prevent execution

**Priority:** 🟡 Medium
**File(s):** `src/ArmRipper.Core/Rip/Conductor.cs`
**Status:** ✅ Done

---

## Problem

At the top of `ProcessJobAsync`, a status guard checks whether the job is `Active`:

```csharp
// Conductor.ProcessJobAsync — lines 610-615
if (job.Status != JobState.Active)
{
    var msg = $"Setup stage: expected status Active, was {job.Status}";
    logger.LogWarning(msg);
    job.Warnings = string.IsNullOrEmpty(job.Warnings) ? msg : $"{job.Warnings}; {msg}";
}
// ← Execution continues — IdentifyAsync still runs!
```

If a job is `Failure`, `Stopping`, or `Cancelled`, execution proceeds through
`IdentifyAsync`, `MarkStageComplete`, and `SaveChangesAsync` before the first
`IsCancelledAsync` guard at line 639.

## Proposed Fix

Return early for non-resumable, non-Active states:

```csharp
if (job.Status != JobState.Active)
{
    if (job.Status.IsResumable())
    {
        logger.LogInformation("Job {JobId} is resumable ({Status}) — proceeding", job.Id, job.Status);
    }
    else
    {
        logger.LogWarning("Job {JobId} has non-Active status {Status} — aborting", job.Id, job.Status);
        return 1;
    }
}
```

This gates execution while allowing the resume path (`Stopping`/`Cancelled` → `Active`
set by the controller) to proceed normally.

## Benefits

- Prevents accidental re-identification of failed discs
- Makes the guard a real safety check, not just a warning
