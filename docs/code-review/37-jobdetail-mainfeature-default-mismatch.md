# JobDetail assumes `MainFeature = true` when job Config is null

**Priority:** 🟢 Low
**File(s):** `src/ArmRipper.WebUi/Views/Jobs/JobDetail.cshtml`, `src/ArmRipper.WebUi/Controllers/ApiController.cs`
**Status:** ⬜ Todo
**Found in:** PR #72 (mid-rip main-feature redirect)

---

## Problem

The Job Detail page decides whether to show the "Redirect" buttons with:

```razor
var trackTableMainFeature = Model.Config?.MainFeature ?? true;
```

but the rip pipeline and the API decide main-feature mode with the **effective**
settings:

```csharp
// ApiController.RedirectRip
var effective = await settingsService.GetEffectiveAsync(ct);
var mainFeatureMode = job.Config?.MainFeature ?? effective.MainFeature;
```

When the global default is `MainFeature = false` and the job has no per-job
`Config` row, the view renders the Redirect buttons even though the job is not
ripping in main-feature mode. Clicking one only persists the override
(`cancelled: false`), which contradicts the button tooltip ("Cancel the current
rip and re-rip this track as the main feature").

## Proposed Fix

Compute the effective main-feature flag in the controller and pass it to the
view (e.g. via `ViewData`), or mirror the effective-settings lookup in the
view's model. Then:

```razor
var trackTableMainFeature = Model.Config?.MainFeature ?? ViewData["MainFeature"] as bool? ?? false;
```

Alternatively, keep the view logic but make the API message clearer when no rip
will be cancelled (see finding #38).
