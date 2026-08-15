# Redirect can report `cancelled = true` without a re-rip

**Priority:** 🟢 Low
**File(s):** `src/ArmRipper.Core/Rip/RipRedirectService.cs`, `src/ArmRipper.WebUi/Controllers/ApiController.cs`
**Status:** ⬜ Todo
**Found in:** PR #72 (mid-rip main-feature redirect)

---

## Problem

`RipRedirectService.RequestRedirect` returns `true` whenever it finds and
cancels a registered cancellation source — even if MakeMKV already exited and
the rip loop has already broken. In that window the API responds with
`cancelled: true` and the UI tells the user "the rip will restart", but the
pipeline never re-rips (the choice is only persisted for the *next* rip of the
disc).

The window is narrow (between MakeMKV's final exit and `EndRip` removing the
CTS) but the user-visible message is misleading.

## Proposed Fix

Track whether the rip loop is still actively ripping (e.g. an `isRipping`
flag set around the `RipTrackAsync` await and cleared when the loop breaks), and
have `RequestRedirect` return whether the cancellation actually aborted an
in-flight rip. Alternatively, soften the UI copy when `cancelled` is true but
the job is no longer in `VideoRipping`:

```csharp
var stillRipping = job.Status.IsRippingState();
return Json(new { success = true, job = id, track = trackNumber, cancelled = cancelled && stillRipping, mainFeatureMode });
```

Note: the stale `_redirectPending` entry left when a redirect lands after
`EndRip` is a related minor leak — the flag is only cleared on the next
`BeginRip`/`EndRip` for the same job, which never happens after the loop exits.
