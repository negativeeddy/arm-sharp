# Deep-Review `IdentifyService.IdentifyVideoDiscAsync`

**Priority:** 🟡 Medium
**File:** `src/ArmRipper.Core/Rip/IdentifyService.cs`
**Status:** ⬜ Todo

---

## Problem

`IdentifyVideoDiscAsync` (the inner method called from `IdentifyAsync`) chains multiple metadata
sources: DiscDb content hash lookup → OVID fingerprint → TMDB/OMDB title search. This is the
method that populates `job.Title`, `job.Year`, `job.VideoType`, `job.ImdbId`, and
`job.PosterUrl`.

Only `IdentifyAsync` (the outer shell) was reviewed. The inner method likely has:

- **API fallback chains:** What happens when all providers fail?
- **Retry logic:** Are transient HTTP errors retried?
- **Title/year extraction:** Regex or heuristic parsing of disc labels
- **Video type detection:** "movie" vs "series" classification logic
- **Manual wait prompting:** Does it signal the UI for user confirmation?

Wrong metadata is a top user complaint in the original ARM project.

## Investigation Tasks

1. Read `IdentifyVideoDiscAsync` fully
2. Map the provider fallback chain and verify each step has error handling
3. Verify title/year extraction handles edge cases (empty labels, Unicode, special characters)
4. Check that `VideoType` classification has a clear fallback (e.g., "movie" if uncertain)
5. Verify that a total metadata failure still produces a job (just with `Title = "Unknown"`)

## Deliverable

After deep review, either:
- Close with "identification logic is robust"
- Create new sub-documents for any metadata-resolution bugs found
