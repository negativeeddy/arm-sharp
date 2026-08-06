# Raw `Contains("Rip")` on `CompletedStages` bypasses `IsStageComplete` abstraction

**Priority:** 🟡 Medium
**File(s):** `src/ArmRipper.Core/Rip/Conductor.cs`
**Status:** 🔄 In Progress

---

## Problem

`JobDupeCheckAsync` (line 1006) checks whether an in-flight job has completed the Rip
stage by doing raw string matching on the pipe-delimited column:

```csharp
// Conductor.cs line 1006
.Where(j => !string.IsNullOrEmpty(j.CompletedStages) && j.CompletedStages.Contains("Rip"))
```

This bypasses the `job.IsStageComplete(RipStage.Rip)` abstraction.  While it happens to
work today (no other stage name contains "Rip" as a substring), it's fragile:

- If a stage like `PreRip` or `CrcRip` is added, the `LIKE '%Rip%'` would match it.
- The separator (`|`) isn't respected — `"CrcSubmitted|PreRip"` would false-match.
- Someone reading the query doesn't immediately know it's checking for `RipStage.Rip`.

## Proposed Fix

Since this is an EF Core query that runs server-side, `IsStageComplete` can't be called
directly.  Use `EF.Functions.Like` with a pipe-delimited-aware pattern:

```csharp
.Where(j => EF.Functions.Like(j.CompletedStages ?? "", $"%|{nameof(RipStage.Rip)}|%")
         || EF.Functions.Like(j.CompletedStages ?? "", $"{nameof(RipStage.Rip)}|%")
         || EF.Functions.Like(j.CompletedStages ?? "", $"%|{nameof(RipStage.Rip)}")
         || j.CompletedStages == nameof(RipStage.Rip))
```

Better yet, extract this into a helper:

```csharp
// On Job model or as an extension for use in LINQ-to-Entities
public static bool EfIsStageComplete(string? completedStages, RipStage stage)
{
    // ... pipe-aware LIKE match as above
}
```

And then call `EfIsStageComplete` from query expressions.

## Benefits

- Robust against future stage name additions
- Pipe-delimiter-aware (won't match partial substrings)
- Uses `nameof` so stage renames break at compile time, not silently at runtime
