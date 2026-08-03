# Plan: DB-First Settings — Remove File/DB Ambiguity

> Status: Phases 0–2 complete & committed on `feature/db-first-settings`
> (Phase 1: `b92db80`; Phase 2: `9520851`); not yet merged to `master`.
> Phases 3–5 not started.
> Goal: Make the database the single source of truth for *runtime* settings. Files
> (`appsettings.json` + `/etc/arm/config/arm.yaml`) become **seed-only** — they can
> never override the DB after first boot.
>
> **Decision (2026-08-02): drop the "drop-in ARM replacement" goal.** ARM is a legacy
> config format, not a runtime source of truth. The automatic YAML overlay at startup
> is removed. `/etc/arm/config/arm.yaml` is only read by an explicit, user-initiated
> **"Import ARM settings"** action that pulls legacy values into the DB once — it
> never overwrites values already stored in the DB. `ArmYamlConfigLoader` remains as
> import tooling only.

---

## 0. Executive summary (answers to the open questions)

**Should we remove the JSON files altogether?**
No. `appsettings.json` is required for structural bootstrap: the `ConnectionStrings`
(SQLite path) and `Logging`. It should **stop acting as a runtime override layer** for
DB-backed settings. The `Arm:` section is largely redundant with code defaults and
should be pruned to near-zero. `/etc/arm/config/arm.yaml` is **no longer loaded at
startup** — it exists only for the explicit **"Import ARM settings"** action (one-time
migration for existing ARM installs).

**Should we remove all the duplicate values?**
Yes — every value that has two homes is a conflict waiting to happen. The concrete
duplicates today: API keys (`Arm:OmdbApiKey`/`Arm:TmdbApiKey`/`Arm:TvdbApiKey` in the
DB blob **and** `Omdb:ApiKey`/`Tmdb:ApiKey`/`Tvdb:ApiKey`/`OvidProvider:ApiToken` in
appsettings), plus the alias properties (`PreventTrack99`, `AudioMetadataProvider`,
`DeleteRawFiles`) that map onto canonical names.

**What makes the most sense?**
A **DB-first model with delta overrides**:

```
class defaults  ←  appsettings.json      →  "file defaults" (seed)
                                                   │
                                                   ▼  (empty row only — never writes file values)
              ripper_settings (DB) stores ONLY user-changed overrides   ←  runtime writes
                                                   │
      /etc/arm/config/arm.yaml ──(explicit "Import ARM settings")──┘  (one-time, never overwrites)
                                                   │
                                                   ▼
        ISettingsService.GetEffectiveAsync()  =  file defaults + DB overrides  (DB wins)
```

Key insight: today the DB stores a **full snapshot** of `ArmSettings` (every property,
including code defaults). That is the root cause of drift — a stale DB snapshot shadows
new code defaults (see the `MinLength` 600→300 hack). Switching the DB to store **only
overrides** (deltas) makes new defaults appear automatically and makes "file wins / DB
wins" a non-question: **DB always wins, and if the DB has no value for a key, the file
default applies.**

---

## 1. Current state

### 1.1 Settings sources (5 layers)

| # | Source | What it holds | Role today |
|---|--------|---------------|------------|
| 1 | C# class defaults (`ArmSettings`, `*Options`) | Defaults per property | Lowest priority |
| 2 | `appsettings.json` (WebUi + Cli) | `Arm:` section (paths, port), provider sections (`Omdb:`, `Tmdb:`, `Tvdb:`, `OvidProvider:`, `DvdCompare:`, `Naming:`, `Linting:`, `EpisodeIdentification:`), `ConnectionStrings`, `Logging` | Seed layer |
| 3 | `/etc/arm/config/arm.yaml` | 50+ `UPPER_CASE` keys → `Arm:*` only (`ArmYamlConfigLoader`) | Seed layer (ARM compat) |
| 4 | `ripper_settings` table (single row) | **Entire `ArmSettings` serialized to a JSON blob** (`SettingsJson`) | Highest priority |
| 5 | `Job.Config` (`ConfigSnapshot`) | Per-job frozen copy of effective settings | Frozen at job start |

### 1.2 Precedence logic today

- `SettingsHelper.GetEffectiveSettingsAsync` = file defaults, then **DB blob values
  override**. This is the intended "DB wins" rule.
- `SettingsHelper.SeedFromFileAsync` writes the **whole** file `ArmSettings` into the
  DB on first boot, and `ARM_RESET_SETTINGS=true` **overwrites the DB with file
  values** — a file-wins escape hatch that reintroduces ambiguity.
- `SettingsHelper.MergeIntoDbAsync` merges individual keys into the DB blob (this is
  already delta-like and is the "good" part).

### 1.3 Concrete conflict vectors (bugs & risks)

1. **Stale full snapshots shadow new code defaults.** Because seeding writes every
   property, a DB row from an older build can override a newly-changed code default.
   Proof: the `MinLength` 600→300 special-case hack in `SettingsHelper` (~L80).
2. **Direct `IOptions<ArmSettings>` reads bypass the merge.** Many core services read
   `settings.Value` and never see DB overrides:
   - `Rip/ArmRipperService.cs` — paths, `SkipTranscode`, `DelRawFiles`, `MinLength`,
     `MaxLength`, `NotifyRip`, `MainFeature`, `DiscDbEnabled`, `TestMode`, etc.
     (mitigated only by `job.Config` being used first for most)
   - `Rip/HandBrakeService.cs`, `Rip/FfmpegService.cs`, `Rip/MakeMkvService.cs`,
     `Rip/MusicBrainzService.cs`, `Rip/DatabaseSubmitService.cs`,
     `Rip/DiscDbQueryService.cs`
   - `Rip/IdentifyService.cs` L921/L1006 — reads `settings.Value.OmdbApiKey` /
     `MetadataProvider` directly (already documented in
     `/memories/repo/omdb-settings.md`)
   - `Rip/Conductor.cs` L485 `Setup()` — creates directories from `settings.Value`
     (raw/transcode/completed/log paths) and never consults the DB
   - `Rip/Conductor.cs` L846/L849 — path fallbacks use `settings.Value`
3. **Duplicate API-key homes.** `Arm:OmdbApiKey` (DB blob, written by `SaveMetadata`)
   vs `Omdb:ApiKey` (appsettings). The resolvers (`OmdbApiKeyResolver`, `TmdbApiKeyResolver`,
   `TvdbApiKeyResolver`, `OvidApiTokenResolver`) paper over this by checking the DB
   first, then the file section. Two homes for one value.
4. **File-wins escape hatch.** `ARM_RESET_SETTINGS=true` (WebUi `Program.cs` + CLI
   `Program.cs`) overwrites DB with file values — violates "DB wins."
5. **Not-all-settings-in-DB, not-all-in-file.** Provider options (`DvdCompare`,
   `EpisodeIdentification`, `Naming`, `Linting`, `FileBot`) are file-only and never in
   the DB; several `ArmSettings` props (`EjectCooldownSeconds`, `DiscDbMinConfidence`,
   `OvidSubmitEnabled`, ...) are DB-capable but not surfaced in the Settings UI. The
   boundary is undocumented → every new feature has to guess.
6. **Alias properties.** `PreventTrack99`/`AudioMetadataProvider`/`DeleteRawFiles` map
   to canonical names via `AliasToCanonical` — extra surface for rename drift.

---

## 2. Target state (design)

### 2.1 One-way flow, DB wins

- **Runtime writes** → only ever hit the DB (via the Settings UI / `ISettingsService`).
- **Runtime reads** → only ever through `ISettingsService.GetEffectiveAsync()` which
  returns `file defaults + DB overrides`. No component reads `IOptions<ArmSettings>`
  directly for DB-backed settings.
- **Files** → merged once at startup into an immutable "file defaults" baseline
  (`appsettings.json` + code defaults). They never write into the DB and are never read
  at runtime as an override source.
- **`/etc/arm/config/arm.yaml` is not loaded at startup.** It is read only by the
  explicit **"Import ARM settings"** action (see §2.6), which pulls legacy values into
  the DB as overrides without overwriting existing DB values.
- **Remove `ARM_RESET_SETTINGS`.** Replace with a UI action "Reset to defaults" that
  **clears all DB overrides** (deletes the delta row) rather than re-writing file values.

### 2.2 `ripper_settings` stores deltas, not snapshots

- DB row holds a JSON object of **only the keys the user changed** (e.g.
  `{"MinLength":450,"DelRawFiles":true}`).
- `GetEffectiveSettingsAsync` = apply DB deltas over file defaults. No key in the DB →
  file/code default wins. New code defaults automatically apply to everyone who hasn't
  explicitly overridden that key → the `MinLength` hack dies.
- `SeedFromFileAsync` is replaced by `EnsureSeededAsync` which **no-ops unless the row
  is empty/missing**; it never copies file values into the DB.
- `MergeIntoDbAsync` is kept as-is (already delta semantics), but gains a
  "null/empty clears the key" path so the UI can reset a single field back to default.

### 2.3 Single resolver / settings service

New `ISettingsService` (scoped, lightweight in-memory cache per request or short TTL):

```csharp
public interface ISettingsService
{
    Task<ArmSettings> GetEffectiveAsync(CancellationToken ct = default);
    Task MergeAsync(Dictionary<string, string?> fields, CancellationToken ct = default);
    Task ClearAllAsync(CancellationToken ct = default);
}
```

All DB-backed reads migrate to it. `SettingsHelper` becomes an internal implementation
detail (or is folded in) — no longer called from scattered controllers/services.

### 2.4 Remove duplicate homes

- **API keys**: canonical home = DB delta keys (`OmdbApiKey`, `TmdbApiKey`, `TvdbApiKey`,
  `OvidApiToken`) on `ArmSettings`. Delete the `Omdb:ApiKey`, `Tmdb:ApiKey`,
  `Tvdb:ApiKey`, `OvidProvider:ApiToken` appsettings sections as *runtime* sources.
  Keep file values (YAML `OMDB_API_KEY`, etc.) **only as first-boot seed** for a brand
  new install. The four resolvers collapse into one call to `ISettingsService`.
- **Alias properties**: keep for one release as read-only legacy shims, then remove
  `AliasToCanonical` once the one-time migration rewrites alias keys.
- **`Arm:` section in `appsettings.json`**: prune to what YAML does not cover (or
  delete entirely — code defaults + YAML already cover paths/port).

### 2.5 Explicit file-only vs DB-backed boundary

Every setting gets exactly one classification, documented in `docs/configuration.md`:

| Classification | Writable in UI | Stored in | Examples |
|----------------|----------------|-----------|----------|
| **User settings (DB-backed)** | Yes | `ripper_settings` deltas | rip/transcode/notification/metadata/API keys |
| **Deployment/bootstrap (file-only)** | No (edit file + restart) | `appsettings.json`/YAML | `ConnectionStrings`, `Logging`, `DvdCompare`, `EpisodeIdentification`, `Naming`, `Linting`, `FileBot` |

Startup validation warns if a key is both DB-overridden *and* file-set for a
file-only classification, so new features can't accidentally reintroduce drift.

### 2.6 Retiring the ARM drop-in replacement — explicit "Import ARM settings"

The ARM drop-in replacement goal is retired. ARM's `/etc/arm/config/arm.yaml` is a
**legacy import source**, not a runtime config layer.

- **Removed:** the `ArmYamlConfigLoader.LoadYamlValues(...)` → `AddInMemoryCollection`
  overlay in both `WebUi/Program.cs` and `Cli/Program.cs`.
- **Added:** `ArmSettingsImporter.ImportFromYamlAsync(db, fileSettings)` — an explicit,
  user-initiated action ("Import ARM settings" button in the Settings UI) that:
  - reads `/etc/arm/config/arm.yaml` via `ArmYamlConfigLoader` (unchanged key map),
  - converts each `Arm:PropertyName` value to the typed `ArmSettings` property,
  - merges it into the DB **only if that key is not already present** in the DB delta
    (DB always wins),
  - reports how many were imported vs skipped.
- **Non-goal:** ARM-specific config (ABC dE, `abcde.conf`, etc.) beyond `ArmSettings`
  stays out of scope.

---

## 3. Phased plan

### Phase 0 — Inventory & observability (small, high value)
- Add a startup diagnostic log: per DB-backed key, print `default | file | db-override`
  so drift is immediately visible in logs (`DatabaseHelper`/`Program` startup).
- Add a `docs/configuration.md` table asserting each `ArmSettings` property's
  classification (DB-backed vs file-only) and UI surface.
- **Exit:** a single source of truth table exists; startup prints the resolution.

### Phase 1 — DB stores deltas (core fix) ✅ done (committed `b92db80`, not merged)
1. ✅ `SeedFromFileAsync` → `EnsureSeededAsync`: creates an empty `{}` row only when the
   row is missing; never writes file values into the DB.
2. ✅ One-time data migration: `NormalizeLegacyRowAsync` converts an existing full-snapshot
   row (≥25 keys) into a delta: drop keys equal to the current file default, keep keys
   that differ (real overrides), drop the stale `MinLength=600`. Idempotent, runs at
   startup.
3. ✅ Remove the `MinLength` 600→300 hack from `GetEffectiveSettingsAsync` (kept only as a
   legacy-snapshot safety net).
4. ✅ Remove `ARM_RESET_SETTINGS` handling in WebUi `Program.cs` and CLI `Program.cs`.
5. ✅ Add `ClearAllAsync` (per-key clear already handled by `MergeIntoDbAsync`).
6. ✅ Add `ArmSettingsImporter` + `SettingsController.ImportArmSettings` + UI button.
7. ✅ Update `SettingsController.ResetSettings` to call `ClearAllAsync` ("Reset to
   defaults" = clear overrides).
- **Exit (met):** a fresh install writes `{}` to `ripper_settings`; old installs migrate to
  delta-only; no code paths copy file→DB at boot; ARM YAML is import-only.
  Verified at runtime: dev DB row migrated from full snapshot → 13-key delta.

### Phase 2 — One resolver, zero direct reads ✅ done (committed, not merged)
1. ✅ Implement `ISettingsService` (`GetEffectiveAsync`/`MergeAsync`/`ClearAllAsync`/
   `EnsureSeededAsync`/`NormalizeLegacyRowAsync`, wrapping `SettingsHelper`), register
   scoped in both WebUi and CLI `Program.cs`.
2. ✅ Route all existing correct callers through it: `SettingsController`
   (Index + all Save*/Reset), `ApiController`, `CompletedController` (incl. `ResolveSource`),
   `JobsController`, `LogsController`, `ReIdentifyController`, `NotificationHub`,
   MCP `ArmRipperTools` (`GetLog`, `GetConfig`), `Conductor`, `BackgroundRipService`
   (per-scope), `DiscPollingService` (per-scope).
3. ✅ Fix the flagged direct-read bugs by resolving via the service once per
   job/scope:
   - `Conductor.Setup()` (L485) — dirs now created from effective settings, not
     `settings.Value`; `RunAsync` resolves once and passes it in.
   - `Conductor` L846/L849 — raw/completed path fallbacks use effective settings.
   - `IdentifyService` L921 `OmdbApiKey` + L1006 `MetadataProvider` +
     `OmdbSearchAsync`/`TmdbSearchAsync` (API keys now passed in from effective).
   - `MakeMkvService.EnsureKeyAsync` — `MakeMkvPermaKey` from effective settings.
   - `MusicBrainzService.IdentifyAsync` — `GetAudioTitle` from effective settings.
   - Remaining `job.Config?.X ?? settings.Value.X` reads (ArmRipperService,
     HandBrakeService, FfmpegService, MakeMkv MinLength/MaxLength, IdentifyService
     DiscDbEnabled/GetVideoTitle/Prevent99/AutoEject) are file-defaults fallbacks
     *after* the per-job `ConfigSnapshot` (which is captured from effective settings at
     job start) — deliberately left as-is; no live DB value is bypassed.
4. ✅ Collapse the four key resolvers (`OmdbApiKeyResolver`, `TmdbApiKeyResolver`,
   `TvdbApiKeyResolver`, `OvidApiTokenResolver`) to a single
   `ISettingsService.GetEffectiveAsync()` read — all DB-blob parsing and file fallbacks
   now live in one place.
- **Exit (met):** every DB-backed setting is read through `ISettingsService` (or a
  per-job `ConfigSnapshot` captured from it). `IOptions<ArmSettings>` remains only for
  file-defaults fallbacks and the ARM-import path (`SettingsController.ImportArmSettings`).
  Grep: `SettingsHelper.GetEffectiveSettingsAsync` has zero *runtime* callers left —
  only one startup bootstrap in WebUi `Program.cs` (resolves the file logger's
  fallback `LogPath` before the DI scope is set up) and the `SettingsService` wrapper.
  Build green (0 warnings), Core 149 pass / 20 pre-existing MakeMkv harness NREs,
  WebUi 68/68, ArmMedia 104/104.

### Phase 3 — Remove duplicate values
1. ✅ Delete `Omdb:ApiKey`, `Tmdb:ApiKey`, `Tvdb:ApiKey`, `OvidProvider:ApiToken` from
   `appsettings.json` / `appsettings.Development.json` (keys now live only in DB
   deltas, pulled in via the explicit "Import ARM settings" action from YAML).
   - Removed from `WebUi/appsettings.json`, `WebUi/appsettings.Development.json`,
     `Cli/appsettings.json`. Kept `Tvdb:ApiBaseUrl` (non-secret URL constant).
   - Deleted `OmdbProviderOptions.cs` / `TmdbProviderOptions.cs`; removed
     `ApiKey`/`ApiToken` from `TvdbProviderOptions` / `OvidProviderOptions`.
   - Dropped the dead `Configure<OmdbProviderOptions>` / `Configure<TmdbProviderOptions>`
     DI registrations in `ArmSharpServiceCollectionExtensions`.
2. ✅ Remove alias properties + `AliasToCanonical`. `PreventTrack99`/`DeleteRawFiles`/
   `AudioMetadataProvider` had no external consumers, so the aliases were removed
   outright from `ArmSettings` / `ConfigSnapshot` and `AliasToCanonical` from
   `SettingsHelper` (Phase 1 migration already rewrote legacy keys to canonical names).
3. ✅ Prune `Arm:` section in the WebUi `appsettings.json` to only non-YAML keys — it
   now holds just `CompletedPath`. The CLI `appsettings.json` retains its `Arm:`
   defaults as the CLI's seed source. **Drift note:** the C# `ArmSettings` defaults
   differ from the CLI file for `HbArgsDvd`/`HbArgsBd` (e.g. `--comb-detect --decomb`,
   `--quality 22` vs `18`) — reconcile the C# defaults in a follow-up rather than
   silently removing the file keys and changing CLI behavior.
- **Exit:** no configuration value exists in more than one place (API keys live only
  in DB deltas; aliases gone; appsettings hold seed defaults only, DB overrides win).
- Committed as `feature/db-first-settings-phase3` (not merged).

### Phase 4 — Enforce the file-only/DB-backed boundary
1. Add a startup validator that flags file-only keys present in DB deltas (and
   DB-backed keys that are absent from the UI).
2. Add explicit "Advanced / file-only" documentation in `docs/configuration.md` and a
   note in `Settings/Index.cshtml` clarifying that those are deployment-only.
3. Update `Setup.cshtml` copy ("File config seeds the DB on first run" → accurate
   wording).
- **Exit:** new features know where their setting lives by convention + validation.

### Phase 5 — Docs & tests
1. Rewrite `docs/configuration.md` hierarchy section with a mermaid precedence diagram
   and the classification table.
2. Tests:
   - `SettingsHelper`: delta semantics (empty row → file defaults; delta overrides;
     null clears a key; legacy full-snapshot row migrates correctly).
   - `ISettingsService`: caching + DB-wins precedence.
   - Resolver path: DB key wins over file seed; fresh install seeds from YAML.
   - `ResetSettings` clears overrides instead of copying file values.
3. Update `ARCHITECTURE.md` line ~119 and any DESIGN docs that reference the old
   hierarchy.
- **Exit:** build green, tests green, docs consistent.

---

## 4. Risks & migration notes

- **Data loss risk during Phase 1**: the full-snapshot → delta migration must be
  conservative — only drop keys equal to the *current* code default. Run it idempotently
  and log a per-key decision so it can be audited. (This mirrors the existing
  `MinLength` special case but generalizes it.)
- **YAML is import-only**: ARM values reach the DB only when the user clicks "Import
  ARM settings" (or, on a brand-new install, whatever they set in the UI). Editing
  `/etc/arm/config/arm.yaml` later has no effect until re-imported, and re-importing
  never overwrites DB values — document this prominently.
- **CLI parity**: `ArmRipper.Cli/Program.cs` duplicates the seeding block. Both entry
  points must land Phase 1/3 changes together or behavior diverges.
- **Per-job `ConfigSnapshot`** stays as-is (frozen effective settings at job start) —
  this is correct and does not conflict with DB-first.
- **`UiSettings`** (theme/refresh) is unrelated UI prefs and is out of scope.

---

## 5. Definition of done

1. `ripper_settings` holds **only user overrides**; no boot path writes file→DB.
2. No production code reads `IOptions<ArmSettings>` for DB-backed settings.
3. No value has two homes (API keys consolidated in DB).
4. Files are seed-only; `ARM_RESET_SETTINGS` and the automatic YAML overlay are gone;
   ARM YAML is reachable only via the explicit "Import ARM settings" action.
5. `docs/configuration.md` documents the single hierarchy and the file-only/DB-backed
   boundary; startup validates it.
6. Full test suite green.
