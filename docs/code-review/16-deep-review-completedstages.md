# Deep-Review `CompletedStages` Serialization & Resume Logic

**Priority:** 🔴 Critical
**Files:**
- `src/ArmRipper.Core/Models/Job.cs` (CompletedStages property)
- `src/ArmRipper.Core/Models/RipStage.cs`
- `src/ArmRipper.Core/Rip/Conductor.cs` (resume-from-stage)
- `src/ArmRipper.Core/Infrastructure/Data/ArmDbContext.cs` (EF config)

**Status:** ✅ Done — 5 findings produced (see #32–#36)

---

## Review Results (2026-08-06)

The pipe-delimited serialization is simple and functional. `IsStageComplete` correctly
guards against null/empty/malformed input and returns `false` safely. `MarkStageComplete`
is idempotent with case-insensitive duplicate detection.

However, several patterns in the codebase bypass the `IsStageComplete`/`MarkStageComplete`
abstraction and use raw string matching on the pipe-delimited column — these are fragile.

5 findings produced. See sub-documents:

| # | Finding | Priority |
|---|---------|----------|
| 32 | Raw `Contains("Rip")` on `CompletedStages` bypasses abstraction | 🟡 Medium |
| 33 | `EF.Functions.Like` on `CompletedStages` bypasses abstraction | 🟡 Medium |
| 34 | No concurrency protection on `MarkStageComplete` writes | 🟡 Medium |
| 35 | Misleading comment: "CompletedStages is not queryable via EF" | 🟢 Low |
| 36 | Renaming `RipStage` silently breaks resume for old jobs | 🟢 Low |

### What's working well

- `IsStageComplete` handles null, empty, and malformed strings safely (silently returns `false`)
- `MarkStageComplete` is idempotent via `OrdinalIgnoreCase` comparison
- `ArmRipperService` properly uses the abstraction for Rip/Transcode stage skipping
- Pipe delimiter (`|`) is safe since no stage name contains `|`
- `StringSplitOptions.RemoveEmptyEntries` handles accidental `||` sequences
- 256-char max length is ample for all 7 stages (~70 chars total)

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
