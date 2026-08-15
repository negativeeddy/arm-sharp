# 🔍 ARM-Sharp Code Review — Findings & Progress Tracker

**Date:** 2026-08-06
**Reviewer:** GitHub Copilot (DeepSeek V4 Pro)
**Scope:** Full codebase — .NET 10, ASP.NET Core MVC, ARM ripping pipeline

---

## Summary

The codebase is well-architected with clear domain modeling, proper DI usage, thorough cancellation
propagation, and thoughtful concurrency patterns. The ripping pipeline's stage-based idempotency and
eject cooldown demonstrate careful handling of real-world hardware interaction challenges.

The findings below are broken into discrete, self-contained tasks that can be tackled independently
over time. Each links to a detailed sub-document with the problem description, affected files, and
proposed fix.

---

## Findings Index

### 🔴 Critical (must-fix — correctness / data-loss risk)

| # | Task | Priority | Status | Assignee |
|---|------|----------|--------|----------|
| 1 | [Fix sync-over-async in `StartImportJob`](01-fix-sync-over-async.md) | 🔴 Critical | ✅ Done | — |
| 2 | [Fix `JobLogger` thread-safety](02-joblogger-thread-safety.md) | 🔴 Critical | ✅ Done | — |
| 3 | [Fix `CheckMediaPresent` async gap & missing failure state](03-checkmedia-async.md) | 🔴 Critical | ✅ Done | — |

### 🟡 Medium (correctness / maintainability)

| # | Task | Priority | Status | Assignee |
|---|------|----------|--------|----------|
| 4 | [Convert `VideoType` from string to enum](04-videotype-enum.md) | 🟡 Medium | ✅ Done | — |
| 5 | [Extract `ConfigSnapshot` factory from `Conductor`](05-config-snapshot-factory.md) | 🟡 Medium | ✅ Done | — |
| 6 | [Add debug logging to empty `catch { }` blocks](06-empty-catch-logging.md) | 🟡 Medium | ✅ Done | — |
| 7 | [Fix `NotificationService` condition / comment mismatch](07-notification-condition.md) | 🟡 Medium | ⬜ Todo | — |
| 8 | [Fix hardcoded `ffmpeg` binary name in test-mode path](08-hardcoded-ffmpeg.md) | 🟡 Medium | ⬜ Todo | — |
| 9 | [Break up `RipVisualMediaAsync` into sub-phases](09-break-up-ripvisualmedia.md) | 🟡 Medium | ⬜ Todo | — |

### 🟢 Low (polish / consistency / minor perf)

| # | Task | Priority | Status | Assignee |
|---|------|----------|--------|----------|
| 10 | [Standardize logger usage (ILogger<T> vs ILoggerFactory)](10-standardize-logger.md) | 🟢 Low | ⬜ Todo | — |
| 11 | [Consider `Channel<T>` for `DiscPollingService` events](11-channel-events.md) | 🟢 Low | ⬜ Todo | — |
| 12 | [Consider `WaitForExitAsync` for process management](12-waitforexitasync.md) | 🟢 Low | ⬜ Todo | — |
| 13 | [Pre-size `List<string>` in `ReadAllLinesAsync`](13-presize-list.md) | 🟢 Low | ⬜ Todo | — |

---

### 🔍 Deep Investigation (areas needing deeper review before a fix can be prescribed)

| # | Task | Priority | Status | Assignee |
|---|------|----------|--------|----------|
| 14 | [Audit & fix null-forgiving operators (`!`)](14-null-forgiving-audit.md) | 🔴 Critical | ✅ Done | — |
| 15 | [Deep-review `Conductor.ProcessJobAsync`](15-deep-review-processjobasync.md) | 🔴 Critical | ✅ Done | — |
| 16 | [Deep-review `CompletedStages` resume logic](16-deep-review-completedstages.md) | 🔴 Critical | ✅ Done | — |
| 17 | [Deep-review `MakeMkvOutputParser`](17-deep-review-makemkv-parser.md) | 🟡 Medium | ⬜ Todo | — |
| 18 | [Deep-review `ShutdownJobCancellationService`](18-deep-review-shutdown-cancellation.md) | 🟡 Medium | ⬜ Todo | — |
| 19 | [Deep-review `IdentifyVideoDiscAsync`](19-deep-review-identifyvideodisc.md) | 🟡 Medium | ⬜ Todo | — |
| 20 | [Deep-review `MusicBrainzService` (audio CD path)](20-deep-review-musicbrainz.md) | 🟡 Medium | ⬜ Todo | — |
| 21 | [Deep-review `DefaultLintingEngine`](21-deep-review-linting.md) | 🟢 Low | ⬜ Todo | — |
| 22 | [Deep-review `OvidSubmitService` & `OvidApiClient`](22-deep-review-ovid.md) | 🟢 Low | ⬜ Todo | — |
| 23 | [Audit EF Core migrations vs model consistency](23-ef-core-migration-audit.md) | 🟢 Low | ⬜ Todo | — |
| 24 | [`ProcessJobAsync` has no resume-from-stage logic](24-processjobasync-no-resume-logic.md) | 🔴 Critical | ✅ Done | — |
| 25 | [`RunAsync` always creates new job — resume path is dead](25-runasync-no-resume-overload.md) | 🔴 Critical | ✅ Done | — |
| 26 | [Non-Active status at entry only warns, doesn't gate](26-processjobasync-status-guard-weak.md) | 🟡 Medium | ✅ Done | — |
| 27 | [Synchronous `FirstOrDefault` blocks async pipeline](27-sync-firstordefault-in-async.md) | 🟡 Medium | ✅ Done | — |
| 28 | [After manual wait, Status set to Active unconditionally](28-manualwait-status-overwrite.md) | 🟡 Medium | ✅ Done | — |
| 29 | [Duplicate `RemoveWriter` in duplicate-skip path](29-duplicate-removewriter.md) | 🟢 Low | ⬜ Todo | — |
| 30 | [Default switch case doesn't call `MarkStageComplete`](30-default-case-missing-markstagecomplete.md) | 🟢 Low | ⬜ Todo | — |
| 31 | [`ManualWaitResume` flag not reset on timeout path](31-manualwaitresume-not-reset.md) | 🟢 Low | ⬜ Todo | — |
| 32 | [Raw `Contains("Rip")` on `CompletedStages` bypasses abstraction](32-completedstages-contains-bypass.md) | 🟡 Medium | ✅ Done | — |
| 33 | [`EF.Functions.Like` on `CompletedStages` bypasses abstraction](33-settingscontroller-eflike-bypass.md) | 🟡 Medium | ✅ Done | — |
| 34 | [No concurrency protection on `MarkStageComplete`](34-markstagecomplete-no-concurrency.md) | 🟡 Medium | ⬜ Todo | — |
| 35 | [Misleading comment: "CompletedStages is not queryable"](35-completedstages-queryable-misleading.md) | 🟢 Low | ⬜ Todo | — |
| 36 | [Renaming `RipStage` silently breaks resume for old jobs](36-ripstage-rename-breaks-completedstages.md) | 🟢 Low | ⬜ Todo | — |

---

## Progress Summary

| Status | Count |
|--------|-------|
| ⬜ Todo | 18 |
| 🔄 In Progress | 0 |
| ✅ Done | 18 |

---

## How to Use

1. Pick a task from the index above.
2. Click the link to read the detailed sub-document (problem, affected files, proposed fix).
3. When starting work, update the status to 🔄 **In Progress** and add your name.
4. When done, update to ✅ **Done**.
5. Deep Investigation tasks (14–23) require a review pass **before** a fix can be prescribed.
   Once investigated, either close them or open new fix tasks with concrete steps.

Each sub-document is self-contained — you don't need to read the full review report to complete a
single task. Do them in any order.
