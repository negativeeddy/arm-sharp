# Misleading comment: "CompletedStages is not queryable via EF"

**Priority:** 🟢 Low
**File(s):** `src/ArmRipper.Core/Rip/DatabaseSubmitService.cs`
**Status:** ⬜ Todo

---

## Problem

`DatabaseSubmitService` line 148 has a misleading comment:

```csharp
// Filter out already-submitted in memory (since CompletedStages is not queryable via EF)
.Where(j => !j.IsStageComplete(RipStage.CrcSubmitted))
```

`CompletedStages` **is** queryable via EF — it's a plain `TEXT` column mapped to a
`string?` property.  The inline filter actually calls `IsStageComplete` in memory
(because the `.Where` runs after `.ToListAsync()` or similar), but the comment
suggests it's a limitation rather than a design choice.

## Proposed Fix

Replace the comment with an accurate one:

```csharp
// Filter in memory: IsStageComplete performs client-side string splitting that
// can't be translated to SQL.  The set of unsubmitted jobs is small.
.Where(j => !j.IsStageComplete(RipStage.CrcSubmitted))
```

Or materialize the filter server-side using a pipe-aware `EF.Functions.Like` pattern
(the shared helper from #32), then remove the client-side filter.

## Benefits

- Accurate comments prevent future developers from making wrong assumptions
- May enable a more efficient server-side query if the helper is adopted
