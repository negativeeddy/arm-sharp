# Code Review Tracker

Tracks automated code review findings and their resolution status.

## Review Batches

### 2026-08-20 — Recent Changes Review

**Scope:** Changes since 2026-07-01 (commits `5d4749a` through `c05c81e`)

| Issue | Priority | Title | Status |
|-------|----------|-------|--------|
| [#145](https://github.com/negativeeddy/arm-sharp/issues/145) | 🔴 Critical | Sequential stdout→stderr read can deadlock in RunStreamingAllAsync | Open |
| [#146](https://github.com/negativeeddy/arm-sharp/issues/146) | 🟡 Medium | Conductor overwrites Cancelled job status with Success | Open |
| [#147](https://github.com/negativeeddy/arm-sharp/issues/147) | 🟡 Medium | Shell injection risk in BashNotifyAsync via user-controlled title/body | Open |
| [#148](https://github.com/negativeeddy/arm-sharp/issues/148) | 🟡 Medium | Synchronous SaveChanges() and WaitForExit() in async methods | Open |
| [#149](https://github.com/negativeeddy/arm-sharp/issues/149) | 🟡 Medium | Duplicate exact-lookup logic in OmdbService.GetPosterAsync | Open |
| [#150](https://github.com/negativeeddy/arm-sharp/issues/150) | 🟡 Medium | DatabaseSubmitService sends API key in URL and uses GET for mutation | Open |
| [#151](https://github.com/negativeeddy/arm-sharp/issues/151) | 🟡 Medium | IdentifyService in-place mutation with intermediate saves creates fragile partial state | Open |
| [#152](https://github.com/negativeeddy/arm-sharp/issues/152) | 🟢 Low | Hardcoded 5-minute timeout in MakeMkvService.GetTrackInfoAsync | Open |
| [#153](https://github.com/negativeeddy/arm-sharp/issues/153) | 🟢 Low | Unrealistic file size multiplier in MainFeatureSizeOf fallback | Open |

**Summary:** 9 new issues created (1 critical, 6 medium, 2 low)

## Progress

| Metric | Count |
|--------|-------|
| Total issues created | 9 |
| Critical | 1 |
| Medium | 6 |
| Low | 2 |
| Needs Investigation | 4 (#145, #147, #150, #151) |
