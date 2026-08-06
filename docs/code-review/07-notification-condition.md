# Fix `NotificationService` Condition / Comment Mismatch

**Priority:** 🟡 Medium
**File:** `src/ArmRipper.Core/Notifications/NotificationService.cs`
**Status:** ⬜ Todo

---

## Problem

In `NotificationService.NotifyAsync`, the comment says "Append Job ID if configured" but the
condition checks for `OmdbApiKey`:

```csharp
// Append Job ID if configured
if (cfg?.OmdbApiKey is not null && job is not null)
    title = $"{title} - {job.Id}";
```

This looks like a copy-paste error. There's no logical relationship between having an OMDB API
key and wanting the job ID appended to notification titles. The intent was likely a dedicated
boolean setting (e.g., `AppendJobIdToNotifications` or similar), or this should simply
always append the job ID when a job is available.

## Proposed Fix

### Option A: Always append when job is available

```csharp
// Append Job ID for traceability
if (job is not null)
    title = $"{title} - #{job.Id}";
```

### Option B: Add a dedicated setting

```csharp
// In ArmSettings.cs
public bool AppendJobIdToNotifications { get; set; } = true;

// In NotifyAsync
if (cfg?.AppendJobIdToNotifications == true && job is not null)
    title = $"{title} - #{job.Id}";
```

**Recommendation:** Option A — always appending the job ID is the least surprising behavior
and doesn't add configuration surface area for a trivial feature.
