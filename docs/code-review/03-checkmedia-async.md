# Fix `CheckMediaPresent` Async Gap & Missing Failure State

**Priority:** 🔴 Critical
**File:** `src/ArmRipper.Core/Rip/IdentifyService.cs`
**Status:** ✅ Done

---

## Problem

In `IdentifyService.IdentifyAsync`, `CheckMediaPresent` is called synchronously even though
`CheckMountAsync` (which also does I/O) is async:

```csharp
var mounted = await CheckMountAsync(job, ct);
// ...
if (!CheckMediaPresent(job.DevPath!))       // ← sync call, may block
{
    job.DiscType = DiscType.Unknown;
    logger.LogWarning("No media detected...");
    return;                                  // ← no failure state set
}
```

Two issues:
1. **Sync I/O:** If `CheckMediaPresent` reads sysfs (`/sys/block/sr0/...`), it does blocking I/O
   on the calling thread.
2. **No terminal state:** The job is marked `DiscType.Unknown` but `job.Status` is never set to
   `JobState.Failure`. The caller (`Conductor`) may not detect this as a terminal condition,
   leaving the job in an ambiguous state.

## Proposed Fix

```csharp
if (!await CheckMediaPresentAsync(job.DevPath!, ct))
{
    job.DiscType = DiscType.Unknown;
    job.Status = JobState.Failure;
    job.Errors = "No media detected on device";
    await db.SaveChangesAsync(ct);
    return;
}
```

Also make `CheckMediaPresent` async if it does any I/O:

```csharp
private static async Task<bool> CheckMediaPresentAsync(string devPath, CancellationToken ct)
{
    var sizePath = $"/sys/block/{Path.GetFileName(devPath)}/size";
    if (!File.Exists(sizePath)) return false;
    var content = await File.ReadAllTextAsync(sizePath, ct);
    return content.Trim() != "0";
}
```
