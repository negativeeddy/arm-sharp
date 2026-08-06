# Deep-Review `CompletedStages` Serialization & Resume Logic

**Priority:** 🔴 Critical
**Files:**
- `src/ArmRipper.Core/Models/Job.cs` (CompletedStages property)
- `src/ArmRipper.Core/Models/RipStage.cs`
- `src/ArmRipper.Core/Rip/Conductor.cs` (resume-from-stage)
- `src/ArmRipper.Core/Infrastructure/Data/ArmDbContext.cs` (EF config)

**Status:** ⬜ Todo

---

## Problem

The idempotent resume feature depends on:
- `job.CompletedStages` — a `string` column in the DB
- `job.Stage` — current pipeline stage
- `MarkStageComplete` / `IsStageComplete` — methods on `Job`

The serialization format, parsing logic, and edge cases were not reviewed. Potential risks:

1. **Malformed string:** If `CompletedStages` gets a corrupt value (DB manual edit, migration
   bug), does `IsStageComplete` throw or silently return `false`?
2. **Partial stage completion:** What happens if a process is SIGKILL'd mid-stage? Are files
   left in a half-written state that "resume" would try to re-process?
3. **Stage list drift:** If new stages are added to the enum, what happens when an old job
   with a stale CompletedStages string is resumed?

## Investigation Tasks

1. Read the `CompletedStages` property, `MarkStageComplete`, and `IsStageComplete`
2. Verify the serialization format (comma-separated? JSON array? bitmask?)
3. Verify it's guarded against null/empty/malformed values
4. Test with a manually corrupted value in the DB
5. Check that `Stopping` state correctly captures partial progress before `finally` runs
6. Verify what happens when a job is resumed after adding a new `RipStage` enum value

## Deliverable

After deep review, either:
- Close with "resume logic is correct and robust"
- Create new sub-documents for any bugs discovered
