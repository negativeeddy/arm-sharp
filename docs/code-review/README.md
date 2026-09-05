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

### 2026-09-02 — Recent Changes Review

**Scope:** Changes since the 2026-08-20 review (Manual Selection rip mode PR #144, TV series detection PR #166)

| Issue | Priority | Title | Status |
|-------|----------|-------|--------|
| [#169](https://github.com/negativeeddy/arm-sharp/issues/169) | 🔴 Critical | Race condition can permanently hang a job in Manual Selection wait | Open |
| [#170](https://github.com/negativeeddy/arm-sharp/issues/170) | 🔴 Critical | Manual selection wait has no timeout — can block the drive indefinitely | Open |
| [#171](https://github.com/negativeeddy/arm-sharp/issues/171) | 🟡 Medium | MakeMkvInfoScanTimeoutMinutes setting is dead — MakeMkvService reads static config, not the DB override | Open |
| [#172](https://github.com/negativeeddy/arm-sharp/issues/172) | 🟡 Medium | submitManualSelection() is undefined in the SignalR live-update path — broken Continue Rip button | Open |
| [#173](https://github.com/negativeeddy/arm-sharp/issues/173) | 🟡 Medium | Empty manual selection (deselect all tracks) fails the job with a confusing error | Open |
| [#174](https://github.com/negativeeddy/arm-sharp/issues/174) | 🟡 Medium | TV detection skips label's disc number when the title already has a season | Open |
| [#175](https://github.com/negativeeddy/arm-sharp/issues/175) | 🟢 Low | Task.Delay(Timeout.Infinite) task is never cancelled — minor resource leak | Open |
| [#176](https://github.com/negativeeddy/arm-sharp/issues/176) | 🟢 Low | ManualSelectionTrackNumbers is never cleared after being applied | Open |
| [#177](https://github.com/negativeeddy/arm-sharp/issues/177) | 🟢 Low | Tv vs Series type inconsistency in the UI | Open |
| [#178](https://github.com/negativeeddy/arm-sharp/issues/178) | 🔍 Investigation | db.Entry(job).ReloadAsync(ct) interaction with tracked tracks entities | Open |
| [#179](https://github.com/negativeeddy/arm-sharp/issues/179) | 🔍 Investigation | Whether UI job cancellation actually wakes the manual selection wait | Open |

**Summary:** 11 new issues created (2 critical, 4 medium, 3 low, 2 investigation)

## Progress

| Metric | Count |
|--------|-------|
| Total issues created | 20 |
| Critical | 3 |
| Medium | 10 |
| Low | 5 |
| Needs Investigation | 6 (#145, #147, #150, #151, #178, #179) |
