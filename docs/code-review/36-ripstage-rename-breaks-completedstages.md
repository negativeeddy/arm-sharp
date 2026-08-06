# Renaming a `RipStage` enum value silently breaks resume for old jobs

**Priority:** 🟢 Low
**File(s):** `src/ArmRipper.Core/Models/RipStage.cs`, `src/ArmRipper.Core/Models/Job.cs`
**Status:** ⬜ Todo

---

## Problem

`IsStageComplete` and `MarkStageComplete` use `Enum.ToString()` to produce the string
stored in `CompletedStages`.  If a `RipStage` value is renamed (e.g., `Rip` →
`Extract`), old jobs in the database still have the old name in their
`CompletedStages` column.

When a resuming conductor runs `IsStageComplete(RipStage.Extract)`, the comparison
fails because the stored string is `"Rip"`, not `"Extract"`.  The stage is
incorrectly treated as incomplete and re-executed.

This isn't data-loss, but it's wasteful — a long ripping or transcoding stage would
be re-run unnecessarily.

## Proposed Fix

Either:

**Option A — Document it.** Add a comment on the `RipStage` enum:

```csharp
/// <summary>
/// WARNING: Renaming a value will break resume-from-stage for existing jobs.
/// If you must rename, create a DB migration to update CompletedStages strings.
/// Safe to ADD new values — reorder doesn't matter.
/// </summary>
public enum RipStage { ... }
```

**Option B — Use a stable key.** Store a `[Display(Name = "rip")]` attribute (or a
custom attribute) as the serialized key, so the enum value name can change
independently:

```csharp
public enum RipStage
{
    [StageKey("setup")]     Setup,
    [StageKey("identify")]  Identify,
    [StageKey("rip")]       Rip,
    // ...
}
```

Option A is simpler and sufficient given how rarely stages are renamed.

## Benefits

- Prevents confusing behavior when someone renames a stage and resumes old jobs
- Option B provides complete decoupling between code names and DB values
