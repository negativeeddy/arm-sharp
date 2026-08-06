# Duplicate `RemoveWriter` call in duplicate-skip path

**Priority:** 🟢 Low
**File(s):** `src/ArmRipper.Core/Rip/Conductor.cs`
**Status:** ⬜ Todo

---

## Problem

In the duplicate-skip path, `RemoveWriter` is called both inline (line 665) and in the
`finally` block (line 804):

```csharp
// Line 665 — inline call before return
fileLogProvider.RemoveWriter(job.GetLogFilePath());
return 0;

// ...

// Line 804 — finally block
finally
{
    fileLogProvider.RemoveWriter(job.GetLogFilePath());
}
```

`RemoveWriter` uses `ConcurrentDictionary.TryRemove`, so the second call is a no-op.
But the inline call is dead code that may confuse future readers into thinking the
`finally` block doesn't run for that path.

## Proposed Fix

Remove the inline `RemoveWriter` call on line 665.  The `finally` block handles
cleanup for all exit paths uniformly.

## Verification

- Run a disc that is a known duplicate with `AllowDuplicates = false`.
- Confirm the job log file is closed exactly once.
