# `RunAsync` always creates a new job — resume path is dead

**Priority:** 🔴 Critical
**File(s):** `src/ArmRipper.Core/Rip/Conductor.cs`
**Status:** ⬜ Todo

---

## Problem

`RunAsync` unconditionally calls `SetupJobAsync`, which creates a **brand new** `Job`
entity every time.  There is no overload that accepts an existing job to resume.

When `JobsController.Resume` is called:
1. It sets the old job's `Status = Active`
2. Calls `backgroundRip.StartRip(devPath)`
3. `BackgroundRipService` calls `conductor.RunAsync(devPath)`
4. `RunAsync` → `SetupJobAsync` creates a **new** job — the original is abandoned

```csharp
// Conductor.RunAsync — lines 48-53
public async Task<int> RunAsync(string devicePath, CancellationToken ct = default)
{
    Job? job = null;
    try
    {
        // ... setup ...
        job = await SetupJobAsync(devicePath, ct);   // ← ALWAYS creates new job
        return await ProcessJobAsync(job, ct);
    }
```

The old job sits in the DB as `Active` forever, while a duplicate proceeds.

## Proposed Fix

Add a `RunResumeAsync(Job existingJob, CancellationToken ct)` overload that skips
`SetupJobAsync` and passes the existing job directly into `ProcessJobAsync`.

```csharp
public async Task<int> RunResumeAsync(Job existingJob, CancellationToken ct = default)
{
    try
    {
        var effectiveSetupSettings = await settingsService.GetEffectiveAsync(ct);
        Setup(effectiveSetupSettings);
        return await ProcessJobAsync(existingJob, ct);
    }
    catch (OperationCanceledException) { /* same as RunAsync */ }
    catch (Exception ex) { /* same as RunAsync */ }
}
```

Then update `JobsController.Resume` to call `RunResumeAsync` instead of `StartRip`.
Combine with #24 (stage-skip logic) for a complete resume feature.

## Benefits

- Existing job metadata (title, year, tracks) is preserved
- `CompletedStages` from the original job is honored
- No orphaned duplicate jobs in the database
