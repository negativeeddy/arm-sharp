#!/usr/bin/env bash
# Creates GitHub issues from code review findings
# Run from repo root: bash scripts/create-code-review-issues.sh
set -euo pipefail

REPO="negativeeddy/arm-sharp"

create_issue() {
  local title="$1"
  local labels="$2"
  local body_file="$3"

  echo "Creating: $title"
  local label_args=()
  IFS=',' read -ra label_array <<< "$labels"
  for label in "${label_array[@]}"; do
    label_args+=("--label" "$label")
  done

  gh issue create --repo "$REPO" \
    --title "$title" \
    --body-file "$body_file" \
    "${label_args[@]}" 2>&1 || echo "FAILED: $title"
}

# We'll use a temp dir for issue bodies
TMPDIR=$(mktemp -d)
trap "rm -rf $TMPDIR" EXIT

echo "=== Creating GitHub Issues from Code Review Findings ==="
echo ""

# --- Medium priority issues ---

cat > "$TMPDIR/07.md" << 'BODY'
## Fix `NotificationService` Condition / Comment Mismatch

**Source:** `docs/code-review/07-notification-condition.md`
**File:** `src/ArmRipper.Core/Notifications/NotificationService.cs`

### Problem

In `NotificationService.NotifyAsync`, the comment says "Append Job ID if configured" but the condition checks for `OmdbApiKey`:

```csharp
// Append Job ID if configured
if (cfg?.OmdbApiKey is not null && job is not null)
    title = $"{title} - {job.Id}";
```

This looks like a copy-paste error — there's no logical relationship between having an OMDB API key and wanting the job ID appended to notification titles.

### Proposed Fix

**Option A (recommended):** Always append when job is available:
```csharp
if (job is not null)
    title = $"{title} - #{job.Id}";
```

**Option B:** Add a dedicated `AppendJobIdToNotifications` setting.
BODY
create_issue "Fix NotificationService condition/comment mismatch (copy-paste from OmdbApiKey)" 'code-review,priority: medium' "$TMPDIR/07.md"

cat > "$TMPDIR/08.md" << 'BODY'
## Fix Hardcoded `ffmpeg` Binary Name in Test-Mode Path

**Source:** `docs/code-review/08-hardcoded-ffmpeg.md`
**File:** `src/ArmRipper.Core/Rip/ArmRipperService.cs`

### Problem

In `RipVisualMediaAsync`, the test-mode trim path hardcodes `"ffmpeg"`:
```csharp
var trimResult = await runner.RunAsync("ffmpeg",
    $"-t 30 -i \"{file}\" -c copy -y \"{tmp}\"", timeoutMs: 60_000, ct: ct);
```

But `FfmpegCli` is a configurable setting respected everywhere else. If a user has a custom ffmpeg path or wrapper script, test-mode trim will fail silently.

### Proposed Fix

Use the configurable `settings.Value.FfmpegCli` instead. Also audit for other hardcoded binary names (`makemkvcon`, `HandBrakeCLI`).
BODY
create_issue "Use configurable FfmpegCli setting in test-mode trim path" 'code-review,priority: medium' "$TMPDIR/08.md"

cat > "$TMPDIR/09.md" << 'BODY'
## Break Up `RipVisualMediaAsync` into Sub-Phases

**Source:** `docs/code-review/09-break-up-ripvisualmedia.md`
**File:** `src/ArmRipper.Core/Rip/ArmRipperService.cs`

### Problem

`RipVisualMediaAsync` is ~300 lines doing 17 distinct phases: path computation, duplicate detection, MakeMKV rip, eject, DB reload, test-mode trim, episode identification, transcode, file moves, poster relocation, Emby refresh, permissions, cleanup, notifications, stage transition.

Hard to test, hard to reason about, changes to one phase risk breaking another.

### Proposed Fix

Extract each phase into a private method with clear single responsibility. The orchestrator becomes a readable sequence of await calls. Each method handles its own `SaveChangesAsync`/broadcast and is independently testable.
BODY
create_issue "Break up RipVisualMediaAsync (~300 lines) into testable sub-phases" 'code-review,priority: medium' "$TMPDIR/09.md"

cat > "$TMPDIR/34.md" << 'BODY'
## No Concurrency Protection on `MarkStageComplete` Writes

**Source:** `docs/code-review/34-markstagecomplete-no-concurrency.md`
**File:** `src/ArmRipper.Core/Models/Job.cs`

### Problem

`MarkStageComplete` performs a read-modify-write cycle with no concurrency control. If two processes both call `SaveChangesAsync` after calling `MarkStageComplete` on the same job, one write will silently overwrite the other.

In practice unlikely during the pipeline (one `ProcessJobAsync` per job), but `DatabaseSubmitService` and `IdentifyService` both call `MarkStageComplete(RipStage.CrcSubmitted)` and could overlap.

### Proposed Fix

Add EF Core optimistic concurrency with a row version/concurrency token on `Job`. `SaveChangesAsync` will throw `DbUpdateConcurrencyException` on collision; callers should catch, reload, and retry.
BODY
create_issue "Add concurrency protection to MarkStageComplete read-modify-write" 'code-review,priority: medium' "$TMPDIR/34.md"

# --- Deep review / needs-investigation issues (Medium) ---

cat > "$TMPDIR/17.md" << 'BODY'
## Deep-Review `MakeMkvOutputParser` and `MakeMkvModels`

**Source:** `docs/code-review/17-deep-review-makemkv-parser.md`
**Files:** `src/ArmRipper.Core/Rip/MakeMkvOutputParser.cs`, `MakeMkvModels.cs`, `MakeMkvService.cs`

### Problem

MakeMKV's `--robot` output is line-oriented and fragile. The parser must handle truncated output, Unicode titles, format drift across versions, multi-angle/multi-edition discs, and empty/zero-duration tracks. The original Python ARM has a long history of parser-related bugs.

### Investigation Tasks
1. Read `MakeMkvOutputParser.ParseLine` — understand parsing strategy
2. Read `MakeMkvModels` — check enum definitions match known MakeMKV output codes
3. Check error handling on unparseable lines (skip? throw? log?)
4. Verify `GetTrackInfoAsync` filters tracks by `MinLength` correctly
5. Check for hardcoded assumptions about output ordering
6. Look for `Substring`/`Split` calls that assume fixed-width fields

### Deliverable
Close with "parser is robust" or create sub-documents for bugs found.
BODY
create_issue "Deep-review: MakeMkvOutputParser fragility (truncated output, Unicode, format drift)" 'code-review,priority: medium,needs-investigation' "$TMPDIR/17.md"

cat > "$TMPDIR/18.md" << 'BODY'
## Deep-Review `ShutdownJobCancellationService`

**Source:** `docs/code-review/18-deep-review-shutdown-cancellation.md`
**File:** `src/ArmRipper.WebUi/Services/ShutdownJobCancellationService.cs`

### Problem

This hosted service presumably cancels in-flight rip jobs on SIGTERM/SIGINT. If it doesn't properly cancel CTS tokens, wait for safe stopping, save `CompletedStages`, and respect Docker's 10-second SIGTERM→SIGKILL grace period — then "resumable" jobs won't actually resume.

### Investigation Tasks
1. Locate the file and read how it hooks into `IHostApplicationLifetime`
2. Verify it calls `CancellationTokenSource.Cancel()` on all active jobs
3. Check whether it waits for jobs to finish saving state
4. Check the timeout vs Docker's 10-second default
5. Verify `finally` blocks in `Conductor` and `BackgroundRipService` still run

### Deliverable
Close with "shutdown handling is correct" or create sub-documents for bugs found.
BODY
create_issue "Deep-review: ShutdownJobCancellationService (Docker SIGTERM grace period)" 'code-review,priority: medium,needs-investigation' "$TMPDIR/18.md"

cat > "$TMPDIR/19.md" << 'BODY'
## Deep-Review `IdentifyService.IdentifyVideoDiscAsync`

**Source:** `docs/code-review/19-deep-review-identifyvideodisc.md`
**File:** `src/ArmRipper.Core/Rip/IdentifyService.cs`

### Problem

`IdentifyVideoDiscAsync` chains multiple metadata sources: DiscDb → OVID fingerprint → TMDB/OMDB title search. This populates `job.Title`, `Year`, `VideoType`, `ImdbId`, `PosterUrl`. Only the outer `IdentifyAsync` shell was reviewed.

### Investigation Tasks
1. Read `IdentifyVideoDiscAsync` fully
2. Map the provider fallback chain and verify error handling at each step
3. Verify title/year extraction handles edge cases (empty labels, Unicode, special chars)
4. Check `VideoType` classification fallback
5. Verify total metadata failure still produces a job (with `Title = "Unknown"`)

### Deliverable
Close with "identification logic is robust" or create sub-documents for bugs found.
BODY
create_issue "Deep-review: IdentifyVideoDiscAsync metadata provider fallback chains" 'code-review,priority: medium,needs-investigation' "$TMPDIR/19.md"

cat > "$TMPDIR/20.md" << 'BODY'
## Deep-Review `MusicBrainzService` — Audio CD Path

**Source:** `docs/code-review/20-deep-review-musicbrainz.md`
**File:** `src/ArmRipper.Core/Rip/MusicBrainzService.cs`

### Problem

The audio CD ripping path (MusicBrainz lookup → abcde/flac rip) is entirely separate from video ripping and was not reviewed. Potential risks include different error-handling patterns, audio-specific tool invocation, MusicBrainz API rate limiting, multi-disc album handling, and Unicode in file paths.

### Investigation Tasks
1. Read `MusicBrainzService` fully
2. Verify it uses `ICliProcessRunner` (not direct `Process.Start`)
3. Check for hardcoded audio tool binary names
4. Verify cancellation token propagation
5. Check how `Conductor` routes audio discs to this service
6. Verify multi-disc set handling

### Deliverable
Close with "audio path is correct" or create sub-documents for bugs found.
BODY
create_issue "Deep-review: MusicBrainzService audio CD path (entirely unreviewed)" 'code-review,priority: medium,needs-investigation' "$TMPDIR/20.md"

# --- Deep review / needs-investigation issues (Low) ---

cat > "$TMPDIR/21.md" << 'BODY'
## Deep-Review `DefaultLintingEngine`

**Source:** `docs/code-review/21-deep-review-linting.md`
**Files:** `src/ArmMedia.Linting/DefaultLintingEngine.cs` and related models

### Problem

The linting module validates naming conventions for ripped files. Naming bugs are a top user complaint in the original ARM. Not reviewed during the initial pass.

### Investigation Tasks
1. Read `DefaultLintingEngine` and understand linting rules
2. Check for configurable naming templates
3. Verify edge cases: multi-episode files, special editions, Unicode titles
4. Check whether linting failures block the pipeline or just warn
5. Verify interaction with episode identification pipeline

### Deliverable
Close with "linting engine is correct" or create sub-documents for bugs found.
BODY
create_issue "Deep-review: DefaultLintingEngine naming validation rules" 'code-review,priority: low,needs-investigation' "$TMPDIR/21.md"

cat > "$TMPDIR/22.md" << 'BODY'
## Deep-Review `OvidSubmitService` & `OvidApiClient`

**Source:** `docs/code-review/22-deep-review-ovid.md`
**Files:** `src/ArmRipper.Core/Rip/OvidSubmitService.cs`, `src/ArmMedia.OvidProvider/OvidApiClient.cs`

### Problem

The OVID fingerprint submission pipeline involves OAuth token management, fingerprint registration, and disc metadata submission. Token expiry/renewal bugs could silently fail all submissions. Not reviewed during the initial pass.

### Investigation Tasks
1. Read `OvidSubmitService` and `OvidApiClient`
2. Verify OAuth token lifecycle (refresh before expiry, not just on 401)
3. Check error handling for rate limits, auth failures, network errors
4. Verify submission failures are logged and don't block the rip pipeline
5. Check `OvidSubmitted` flag is correctly set and persisted

### Deliverable
Close with "OVID integration is correct" or create sub-documents for bugs found.
BODY
create_issue "Deep-review: OvidSubmitService OAuth token lifecycle & error handling" 'code-review,priority: low,needs-investigation' "$TMPDIR/22.md"

cat > "$TMPDIR/23.md" << 'BODY'
## Audit EF Core Migrations — Schema vs Model Consistency

**Source:** `docs/code-review/23-ef-core-migration-audit.md`
**Files:** `src/ArmRipper.Core/Migrations/`, `ArmDbContext.cs`, `Models/`

### Problem

Column mismatches between EF Core model and migration files could cause runtime failures on fresh databases or during migration from older versions.

### Investigation Tasks
1. Generate a fresh migration and diff against existing
2. Compare fresh SQL schema with what `EnsureMigrated` produces
3. Check for entity properties with no corresponding column config
4. Verify `HasMaxLength`, `HasConversion`, index configs match migrations
5. Look for orphaned migration files

### Deliverable
Close with "schema and model are in sync" or create sub-documents for mismatches found.
BODY
create_issue "Audit: EF Core migrations vs model consistency" 'code-review,priority: low,needs-investigation' "$TMPDIR/23.md"

# --- Low priority issues ---

cat > "$TMPDIR/10.md" << 'BODY'
## Standardize Logger Usage (`ILogger<T>` vs `ILoggerFactory`)

**Source:** `docs/code-review/10-standardize-logger.md`
**Files:** Multiple across `ArmRipper.Core` and `ArmRipper.WebUi`

### Problem

Inconsistent logger creation: some services use `ILoggerFactory.CreateLogger("Name")` (duplicates class name as string, typo risk, doesn't auto-update on rename) while others use `ILogger<T>` (auto-updates, compile-time safe, .NET convention).

### Proposed Fix

Switch all services to `ILogger<T>`:
```csharp
// Before
public sealed class Conductor(ILoggerFactory loggerFactory, ...) {
    private readonly ILogger logger = loggerFactory.CreateLogger("Conductor");

// After
public sealed class Conductor(ILogger<Conductor> logger, ...) {
    // logger available via primary constructor
```

Verify with: `grep -rn "loggerFactory.CreateLogger" --include="*.cs" src/`
BODY
create_issue "Standardize logger usage to ILogger<T> across codebase" 'code-review,priority: low' "$TMPDIR/10.md"

cat > "$TMPDIR/11.md" << 'BODY'
## Consider `Channel<T>` for `DiscPollingService` Events

**Source:** `docs/code-review/11-channel-events.md`
**File:** `src/ArmRipper.Core/Infrastructure/DiscPollingService.cs`

### Problem

`DiscPollingService` uses a complex state machine with `SemaphoreSlim`, `ConcurrentDictionary`, `CancellationTokenSource`, and manual task management. The interplay is hard to reason about and test.

### Proposed Fix

Replace with `Channel<DiscEvent>` that serializes all disc events — single-threaded processing eliminates `ConcurrentDictionary`, provides backpressure, and the consumer loop is trivially testable.

### When to do
Low priority because current implementation works. Do when adding disc detection features, debugging race conditions, or improving test coverage.
BODY
create_issue "Consider Channel<T> to simplify DiscPollingService event handling" 'code-review,priority: low' "$TMPDIR/11.md"

cat > "$TMPDIR/12.md" << 'BODY'
## Consider `WaitForExitAsync` for Process Management

**Source:** `docs/code-review/12-waitforexitasync.md`
**File:** `src/ArmRipper.Core/Infrastructure/CliProcessRunner.cs`

### Problem

`CliProcessRunner` uses sync `process.WaitForExit(timeoutMs)` with `CancellationToken.Register` callback. Has a subtle race: if process exits as cancellation fires, `process.Kill` may throw `InvalidOperationException`. The `try/catch` mitigates but the pattern is fragile.

### Proposed Fix

Use .NET 9+ `Process.WaitForExitAsync(CancellationToken)` (safe since project targets .NET 10):
```csharp
using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMs));
using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
await process.WaitForExitAsync(linkedCts.Token);
```

No more `CancellationToken.Register` / manual cleanup.
BODY
create_issue "Use WaitForExitAsync instead of sync WaitForExit + CancellationToken.Register" 'code-review,priority: low' "$TMPDIR/12.md"

cat > "$TMPDIR/13.md" << 'BODY'
## Pre-Size `List<string>` in `ReadAllLinesAsync`

**Source:** `docs/code-review/13-presize-list.md`
**File:** `src/ArmRipper.Core/Infrastructure/CliProcessRunner.cs`

### Problem

`ReadAllLinesAsync` creates `List<string>` with default capacity (4). For MakeMKV output (thousands of lines), this triggers multiple internal array resizes and copies.

### Proposed Fix

Pre-size with 256: `var lines = new List<string>(capacity: 256);`

For extreme cases, consider `IAsyncEnumerable<string>` but that changes the API surface.
BODY
create_issue "Pre-size List<string> in ReadAllLinesAsync for large process output" 'code-review,priority: low' "$TMPDIR/13.md"

cat > "$TMPDIR/29.md" << 'BODY'
## Duplicate `RemoveWriter` Call in Duplicate-Skip Path

**Source:** `docs/code-review/29-duplicate-removewriter.md`
**File:** `src/ArmRipper.Core/Rip/Conductor.cs`

### Problem

In the duplicate-skip path, `RemoveWriter` is called both inline (before return) and in the `finally` block. `RemoveWriter` uses `ConcurrentDictionary.TryRemove`, so the second call is a no-op. The inline call is dead code that may confuse future readers.

### Proposed Fix

Remove the inline `RemoveWriter` call. The `finally` block handles cleanup for all exit paths uniformly.
BODY
create_issue "Remove duplicate RemoveWriter call in duplicate-skip path (dead code)" 'code-review,priority: low' "$TMPDIR/29.md"

cat > "$TMPDIR/30.md" << 'BODY'
## Default Switch Case Doesn't Call `MarkStageComplete`

**Source:** `docs/code-review/30-default-case-missing-markstagecomplete.md`
**File:** `src/ArmRipper.Core/Rip/Conductor.cs`

### Problem

When disc type is unknown, the `default` case sets `Failure` but never marks the Identify stage complete. The failure path during identification (line 628) calls `MarkStageComplete(RipStage.Identify)` before returning, but the default case doesn't.

### Proposed Fix

Add `job.MarkStageComplete(RipStage.Identify)` before setting `Failure` in the default case, for consistent `CompletedStages` tracking across all failure paths.
BODY
create_issue "Add MarkStageComplete to default switch case for unknown disc type" 'code-review,priority: low' "$TMPDIR/30.md"

cat > "$TMPDIR/31.md" << 'BODY'
## `ManualWaitResume` Flag Not Reset on Timeout Path

**Source:** `docs/code-review/31-manualwaitresume-not-reset.md`
**File:** `src/ArmRipper.Core/Rip/Conductor.cs`

### Problem

When user clicks "Resume" in the UI, `ManualWaitResume` is correctly reset. But if the timer expires naturally, the flag remains `true` in the database. Downstream code might incorrectly think the user wants to resume.

### Proposed Fix

Reset the flag unconditionally when the loop exits, regardless of why:
```csharp
job.ManualWaitResume = false;
```
BODY
create_issue "Reset ManualWaitResume flag on timeout path (stale flag in DB)" 'code-review,priority: low' "$TMPDIR/31.md"

cat > "$TMPDIR/35.md" << 'BODY'
## Misleading Comment: "CompletedStages is not queryable via EF"

**Source:** `docs/code-review/35-completedstages-queryable-misleading.md`
**File:** `src/ArmRipper.Core/Rip/DatabaseSubmitService.cs`

### Problem

Comment says `CompletedStages` is "not queryable via EF" but it's a plain TEXT column mapped to a `string?` property. The inline filter uses `IsStageComplete` in memory because it does client-side string splitting, not because of an EF limitation.

### Proposed Fix

Replace with accurate comment explaining the real reason (client-side string splitting can't translate to SQL).
BODY
create_issue "Fix misleading comment about CompletedStages EF queryability" 'code-review,priority: low' "$TMPDIR/35.md"

cat > "$TMPDIR/36.md" << 'BODY'
## Renaming `RipStage` Enum Silently Breaks Resume for Old Jobs

**Source:** `docs/code-review/36-ripstage-rename-breaks-completedstages.md`
**Files:** `src/ArmRipper.Core/Models/RipStage.cs`, `src/ArmRipper.Core/Models/Job.cs`

### Problem

`IsStageComplete`/`MarkStageComplete` use `Enum.ToString()` for the stored string. If a `RipStage` value is renamed, old jobs with the old name in `CompletedStages` won't match, causing stages to be incorrectly re-executed on resume.

### Proposed Fix

**Option A (simple):** Add a warning comment on the `RipStage` enum about rename risks.**Option B (robust):** Use `[Display(Name = "rip")]` or custom `[StageKey]` attribute as the stable serialized key.
BODY
create_issue "RipStage enum rename silently breaks resume for old jobs" 'code-review,priority: low' "$TMPDIR/36.md"

cat > "$TMPDIR/37.md" << 'BODY'
## JobDetail Assumes `MainFeature=true` When Job Config is Null

**Source:** `docs/code-review/37-jobdetail-mainfeature-default-mismatch.md`
**Files:** `src/ArmRipper.WebUi/Views/Jobs/JobDetail.cshtml`, `ApiController.cs`

### Problem

The Job Detail page uses `Model.Config?.MainFeature ?? true` but the rip pipeline uses `job.Config?.MainFeature ?? effective.MainFeature`. When global default is `MainFeature=false` and job has no per-job Config, the view shows Redirect buttons even though the job isn't in main-feature mode.

### Proposed Fix

Compute the effective main-feature flag in the controller and pass it to the view via `ViewData`, matching the API's effective-settings lookup.
BODY
create_issue "JobDetail view MainFeature default mismatch with API effective settings" 'code-review,priority: low' "$TMPDIR/37.md"

cat > "$TMPDIR/38.md" << 'BODY'
## Redirect Can Report `cancelled=true` Without a Re-Rip

**Source:** `docs/code-review/38-redirect-cancelled-without-restart.md`
**Files:** `src/ArmRipper.Core/Rip/RipRedirectService.cs`, `ApiController.cs`

### Problem

`RequestRedirect` returns `true` whenever it cancels a registered CTS — even if MakeMKV already exited. In that window the API responds with `cancelled: true` and the UI says "rip will restart", but the pipeline never re-rips.

### Proposed Fix

Track whether the rip loop is actively ripping and have `RequestRedirect` return whether cancellation actually aborted an in-flight rip. Also fix stale `_redirectpending` entry leak.
BODY
create_issue "Redirect reports cancelled=true even when MakeMKV already exited" 'code-review,priority: low' "$TMPDIR/38.md"

cat > "$TMPDIR/39.md" << 'BODY'
## WebUi Integration Tests Flake in `CreateAuthenticatedWithTokenAsync`

**Source:** `docs/code-review/39-webui-test-login-flake.md`
**File:** `tests/ArmRipper.WebUi.Tests/ApiIntegrationTests.cs`

### Problem

Tests flake due to shared `WebApplicationFactory` and parallel DB seeding. The seeded admin user may not be visible to the login handler when tests run in parallel.

### Proposed Fix

- Add retry around login POST in `CreateAuthenticatedWithTokenAsync` (up to 3 times), OR
- Isolate app instances per class with `IClassFixture` on a `CustomWebApplicationFactory` that owns its own in-memory DB.
BODY
create_issue "Fix WebUi integration test flakiness (shared WebApplicationFactory)" 'code-review,priority: low' "$TMPDIR/39.md"

cat > "$TMPDIR/40.md" << 'BODY'
## No Test for Job Override Read from DB with Stale Tracked Entity

**Source:** `docs/code-review/40-missing-stale-entity-override-test.md`
**Files:** `src/ArmRipper.Core/Rip/ArmRipperService.cs`, `tests/ArmRipper.Core.Tests/RipVerificationIntegrationTests.cs`

### Problem

In production, the mid-rip redirect writes `MainFeatureOverrideTrackNumber` via a separate `DbContext` scope. The pipeline holds a stale tracked entity. `ResolveMainFeatureTrackAsync` falls back to a fresh `AsNoTracking` DB read — but no integration test exercises this path for job overrides.

### Proposed Fix

Add an integration test where the override is written via a second `DbContext` while the pipeline's tracked entity is stale, verifying the `AsNoTracking` fallback works.
BODY
create_issue "Add integration test for stale-entity job override (AsNoTracking fallback)" 'code-review,priority: low' "$TMPDIR/40.md"

echo ""
echo "=== Done creating issues ==="
