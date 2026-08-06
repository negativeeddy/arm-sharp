# `EF.Functions.Like` on `CompletedStages` bypasses stage abstraction in SettingsController

**Priority:** 🟡 Medium
**File(s):** `src/ArmRipper.WebUi/Controllers/SettingsController.cs`
**Status:** 🔄 In Progress

---

## Problem

`SettingsController` counts pending CRC submissions using a raw SQL `LIKE` on
`CompletedStages`:

```csharp
// SettingsController.cs line 85
var pendingCrcCount = await db.Jobs
    .Where(j => j.DiscType == DiscType.Dvd &&
                !string.IsNullOrEmpty(j.CrcId) &&
                (j.HasNiceTitle || !string.IsNullOrEmpty(j.TitleManual)) &&
                !EF.Functions.Like(j.CompletedStages ?? "", "%CrcSubmitted%"))
    .CountAsync(ct);
```

Same fragility as #32: `%CrcSubmitted%` doesn't respect the pipe delimiter.  If a
future stage name happens to be a substring of `CrcSubmitted`, this query would
incorrectly exclude those jobs from the pending count.

## Proposed Fix

Use a shared pipe-delimiter-aware helper (same one created for #32):

```csharp
var pendingCrcCount = await db.Jobs
    .Where(j => j.DiscType == DiscType.Dvd &&
                !string.IsNullOrEmpty(j.CrcId) &&
                (j.HasNiceTitle || !string.IsNullOrEmpty(j.TitleManual)) &&
                !JobStageQueryHelper.EfHasStageCompleted(j.CompletedStages, nameof(RipStage.CrcSubmitted)))
    .CountAsync(ct);
```

## Benefits

- Single source of truth for pipe-delimited stage matching
- `nameof` ensures compile-time safety on stage renames
- No silent false positives from substring matches
