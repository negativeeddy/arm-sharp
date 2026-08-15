# No test for job override read from DB with a stale tracked entity

**Priority:** 🟢 Low
**File(s):** `src/ArmRipper.Core/Rip/ArmRipperService.cs` (`ResolveMainFeatureTrackAsync`), `tests/ArmRipper.Core.Tests/RipVerificationIntegrationTests.cs`
**Status:** ⬜ Todo
**Found in:** PR #72 (mid-rip main-feature redirect)

---

## Problem

In production, the mid-rip redirect works like this:

1. The WebUi API controller persists `Job.MainFeatureOverrideTrackNumber` using
   its **own** `DbContext` scope.
2. The rip pipeline holds a **stale tracked `Job` entity** that does not have the
   override set.
3. `ResolveMainFeatureTrackAsync` falls back to a fresh `AsNoTracking` DB read
   so the override is honored anyway.

The integration tests never exercise step 3 for the *job override* path:

- `MainFeatureOverride_HonoredAtSelection_RipsChosenTrack` sets the override on
  the pipeline's in-memory entity directly.
- `FingerprintOverride_HonoredAtSelection_RipsRememberedTrack` exercises the
  fresh DB read, but only for the fingerprint path.
- `MidRipRedirect_CancelsActiveRip_AndReripsChosenTrack` sets the override on
  the same tracked entity, so the stale-entity fallback is bypassed there too.

The most important production behavior — a redirect written by a separate
`DbContext` being picked up by the pipeline — is only indirectly covered.

## Proposed Fix

Add an integration test where the override is written via a second `DbContext`
while the pipeline's tracked entity is stale:

```csharp
// The pipeline's tracked entity has no override set:
Assert.Null(job.MainFeatureOverrideTrackNumber);

// A separate context (as the API controller would) persists the redirect:
using (var other = new ArmDbContext(...))
{
    var dbJob = other.Jobs.First(j => j.Id == job.Id);
    dbJob.MainFeatureOverrideTrackNumber = "2";
    other.SaveChanges();
}

// The rip must still pick track 2 via the AsNoTracking fallback read.
var result = await InvokeAsync(service, job, makeMkvOutPath);
makeMkv.Verify(m => m.RipTrackAsync(It.IsAny<Job>(), "2", ...), Times.Once);
```
