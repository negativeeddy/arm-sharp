# DESIGN: TV Series Strategy & Episode Identification Orchestrator

**Version:** 1.0.0  
**Date:** 2026-06-29  
**Status:** Draft  
**Relates to:** DESIGN-DiscDb-Integration.md

---

## Table of Contents

1. [Overview](#1-overview)
2. [Goals & Non-Goals](#2-goals--non-goals)
3. [Architecture Summary](#3-architecture-summary)
4. [Integration with DiscDb](#4-integration-with-discdb)
5. [Core Abstractions](#5-core-abstractions)
6. [Provider Model](#6-provider-model)
7. [Orchestrator Design](#7-orchestrator-design)
8. [Naming Subsystem](#8-naming-subsystem)
9. [Linting Subsystem](#9-linting-subsystem)
10. [ArmSharp Extensions Glue](#10-armsharp-extensions-glue)
11. [FileBotProvider Stub](#11-filebotprovider-stub)
12. [Configuration Reference](#12-configuration-reference)
13. [Sample Data Shapes](#13-sample-data-shapes)
14. [Unit Testing Strategy](#14-unit-testing-strategy)
15. [CI / Build](#15-ci--build)
16. [Open Issues & Future Work](#16-open-issues--future-work)

---

## 1. Overview

ARM-Sharp (Automated Ripping Machine Sharp) processes optical discs and produces transcoded media files. For TV series discs the system must:

1. **Identify** which track on the disc corresponds to which episode (and whether it is a multi-part episode, a special, or an extra).
2. **Name** the output file according to configurable templates (Plex, Kodi, Jellyfin-compatible).
3. **Lint** the resulting mapping to catch obvious problems before transcoding begins.

This document describes the `EpisodeIdentificationOrchestrator` and the pluggable provider chain that feeds it, plus the integration points with the existing `DiscDbMappingService` and `TrackMapperService` (from DESIGN-DiscDb-Integration.md).

---

## 2. Goals & Non-Goals

### Goals

- Provide a **stable, minimal public API** that third-party providers can implement.
- **Reuse** existing DiscDb lookup and track-mapping infrastructure without duplication.
- Support **multi-part episodes** (e.g., "S01E01E02") and **extras/specials** (Season 0).
- Return a **merged EpisodeMap** that downstream naming and linting steps consume.
- Remain **provider-agnostic**: FileBot, TMDB, TVDB, DiscDb, and manual overrides are all equal citizens.

### Non-Goals

- Does not perform actual transcoding.
- Does not manage TMDB/TVDB API keys or rate-limiting (provider responsibility).
- Does not modify disc data structures.

---

## 3. Architecture Summary

```
┌───────────────────────────────────────────────────────────────────────┐
│  ArmRipperService (ArmMedia.ArmSharpExtensions)                       │
│  PrepareTranscodeInputPathAsync()                                     │
└──────────────────────────┬────────────────────────────────────────────┘
                           │ calls
                           ▼
┌───────────────────────────────────────────────────────────────────────┐
│  EpisodeIdentificationOrchestrator  (ArmMedia.Core)                   │
│  IdentifyAsync(DiscContext) → EpisodeMap                              │
│                                                                       │
│  Provider pipeline (ordered, short-circuits on Definitive confidence) │
│  ┌─────────────────┐  ┌──────────────┐  ┌────────────┐  ┌─────────┐ │
│  │ DiscDbProvider  │→ │FileBotProvider│→ │TmdbProvider│→ │Fallback │ │
│  └─────────────────┘  └──────────────┘  └────────────┘  └─────────┘ │
│                                                                       │
│  Merge strategy: highest-confidence wins per track;                   │
│  multi-part detection applied after merge.                            │
└──────────────────────────┬────────────────────────────────────────────┘
                           │ produces
                           ▼
                    ┌──────────────┐
                    │  EpisodeMap  │
                    └──────┬───────┘
                           │
               ┌───────────┴────────────┐
               ▼                        ▼
     ┌──────────────────┐     ┌──────────────────┐
     │  IEpisodeRenamer │     │  ILintingEngine  │
     │  (ArmMedia.Naming│     │  (ArmMedia.Linting│
     └──────────────────┘     └──────────────────┘
```

---

## 4. Integration with DiscDb

Per DESIGN-DiscDb-Integration.md, the DiscDb integration exposes:

- **`DiscDbMappingService.LookupDiscAsync(string discId) → DiscDbRecord?`** — resolves a disc ID (e.g., Blu-ray barcode or MakeMKV disc hash) to a structured metadata record including series name, season, and per-track episode assignments.
- **`TrackMapperService.MapTracksAsync(DiscDbRecord record, IReadOnlyList<TrackContext> tracks) → IReadOnlyList<MappedTrack>`** — correlates physical tracks with DiscDb episode entries using runtime matching and track-index heuristics.

The `DiscDbProvider` (implemented inside ArmMedia.Core or a separate project) wraps these two services and emits `ProviderResult[]` with `Confidence.Definitive` when a DiscDb record is found, causing the orchestrator to short-circuit.

### Reuse Contract

```csharp
// Injected into DiscDbProvider via constructor DI
private readonly IDiscDbMappingService _discDb;
private readonly ITrackMapperService   _trackMapper;

public async Task<ProviderResult[]> IdentifyAsync(DiscContext ctx, CancellationToken ct)
{
    var record = await _discDb.LookupDiscAsync(ctx.DiscId, ct);
    if (record is null) return [];

    var mapped = await _trackMapper.MapTracksAsync(record, ctx.Tracks, ct);
    return mapped.Select(m => new ProviderResult
    {
        TrackIndex  = m.TrackIndex,
        Season      = m.Season,
        Episodes    = m.Episodes,
        Title       = m.Title,
        Confidence  = Confidence.Definitive,
        ProviderName = "DiscDb"
    }).ToArray();
}
```

---

## 5. Core Abstractions

### 5.1 `IEpisodeIdentificationProvider`

Implemented by each identification source. Providers are stateless and thread-safe.

```csharp
/// <summary>
/// Identifies episodes for the tracks described by <paramref name="context"/>.
/// </summary>
public interface IEpisodeIdentificationProvider
{
    /// <summary>Human-readable name used in logging and reports.</summary>
    string ProviderName { get; }

    /// <summary>
    /// Returns identification results for as many tracks as this provider
    /// can confidently resolve. May return an empty array.
    /// </summary>
    Task<ProviderResult[]> IdentifyAsync(DiscContext context, CancellationToken cancellationToken);
}
```

### 5.2 `ProviderResult`

Immutable record returned by each provider for a single track.

```csharp
public sealed record ProviderResult
{
    public required int         TrackIndex   { get; init; }
    public required int         Season       { get; init; }
    public required int[]       Episodes     { get; init; }  // [1] normal, [1,2] multi-part
    public string?              Title        { get; init; }
    public bool                 IsExtra      { get; init; }
    public Confidence           Confidence   { get; init; }
    public required string      ProviderName { get; init; }
}

public enum Confidence { Low = 0, Medium = 1, High = 2, Definitive = 3 }
```

### 5.3 `DiscContext`

Input to the orchestrator; built by `ArmRipperService`.

```csharp
public sealed class DiscContext
{
    public required string                    DiscId       { get; init; }
    public required string                    SeriesTitle  { get; init; }
    public required int                       Season       { get; init; }
    public required IReadOnlyList<TrackContext> Tracks     { get; init; }
    public string?                            DiscDbHint   { get; init; }
}
```

### 5.4 `TrackContext`

Per-track MakeMKV data surfaced to providers.

```csharp
public sealed class TrackContext
{
    public required int     TrackIndex    { get; init; }
    public required TimeSpan Duration     { get; init; }
    public required long    SizeBytes     { get; init; }
    public int?             ChapterCount  { get; init; }
    public string?          DiscDbTrackId { get; init; }
    public IDictionary<string,string> RawProperties { get; init; } = new Dictionary<string,string>();
}
```

### 5.5 `EpisodeMap` & `MappedTrack`

The final merged output consumed by naming and linting.

```csharp
public sealed class EpisodeMap
{
    public required string              SeriesTitle  { get; init; }
    public required int                 Season       { get; init; }
    public required IReadOnlyList<MappedTrack> Tracks { get; init; }
    public DateTimeOffset               GeneratedAt  { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class MappedTrack
{
    public required int      TrackIndex   { get; init; }
    public required int      Season       { get; init; }
    public required int[]    Episodes     { get; init; }
    public string?           Title        { get; init; }
    public bool              IsExtra      { get; init; }
    public bool              IsMultiPart  => Episodes.Length > 1;
    public required string   WinningProvider { get; init; }
    public required Confidence Confidence  { get; init; }
}
```

### 5.6 `IEpisodeIdentificationOrchestrator`

```csharp
/// <summary>
/// Runs the provider pipeline and returns a merged <see cref="EpisodeMap"/>.
/// </summary>
public interface IEpisodeIdentificationOrchestrator
{
    Task<EpisodeMap> IdentifyAsync(DiscContext context, CancellationToken cancellationToken = default);
}
```

---

## 6. Provider Model

### 6.1 Provider Registration (DI)

Providers are registered in order of preference in `appsettings.json` / DI:

```json
"EpisodeIdentification": {
  "ProviderOrder": ["DiscDb", "FileBot", "Tmdb", "Tvdb"],
  "ShortCircuitOnDefinitive": true
}
```

In `Program.cs` / `Startup`:

```csharp
services.AddEpisodeIdentification(config)
        .AddProvider<DiscDbProvider>()
        .AddProvider<FileBotProvider>()
        .AddProvider<TmdbProvider>();
```

### 6.2 Confidence Levels

| Level | Meaning | Example |
|---|---|---|
| `Definitive` | Exact disc ID match | DiscDb barcode lookup |
| `High` | Strong runtime + metadata match | FileBot with exact match |
| `Medium` | Runtime heuristic match | TMDB runtime ± 3 min |
| `Low` | Positional guess | Fallback: track N → episode N |

---

## 7. Orchestrator Design

### 7.1 Pseudocode

```
IdentifyAsync(ctx):
  allResults ← {}
  
  FOR provider IN orderedProviders:
    results ← await provider.IdentifyAsync(ctx)
    FOR result IN results:
      IF result.Confidence > allResults[result.TrackIndex].Confidence:
        allResults[result.TrackIndex] ← result
    IF shortCircuit AND any result.Confidence == Definitive:
      BREAK

  // Apply multi-part detection
  FOR each track T in allResults:
    adjacentEps ← tracks where |episode - T.episode| == 1
                             AND durations suggest combined episode
    IF adjacentEps found AND runtime matches combined:
      T.Episodes ← [T.Episodes[0], adjacentEps[0].Episodes[0]]
      REMOVE adjacentEps from map (merged)

  // Fill unidentified tracks as Low-confidence positional
  FOR trackIndex IN ctx.Tracks WHERE NOT in allResults:
    allResults[trackIndex] ← PositionalFallback(trackIndex, ctx.Season)

  RETURN new EpisodeMap { Tracks = allResults.Values }
```

### 7.2 Multi-Part Detection Rules

A pair of tracks `A` and `B` is merged into a multi-part episode when **all** of the following hold:

1. `A.Season == B.Season` and `B.Episodes[0] == A.Episodes[0] + 1`
2. `|A.Duration - B.Duration| < MultiPartDurationToleranceSeconds` (default 300 s)
3. Combined duration matches a known "double episode" runtime within `RuntimeToleranceSeconds` (default 180 s)
4. Both providers report `Confidence >= Medium`

### 7.3 Extras / Specials Detection

A track is flagged `IsExtra = true` when:
- Provider explicitly sets it, **or**
- Track duration < `ExtraMaxDurationSeconds` (default 600 s) and no episode assignment matches any season episode count, **or**
- Season = 0 from any source with `Confidence >= Medium`

---

## 8. Naming Subsystem

### 8.1 Interface

```csharp
public interface IEpisodeRenamer
{
    /// <summary>Generates an output file name for the given mapped track.</summary>
    string Rename(MappedTrack track, NamingOptions options);
}
```

### 8.2 NamingOptions

```csharp
public sealed class NamingOptions
{
    public string SeriesTitle      { get; set; } = "";
    public string Template         { get; set; } = "{Series} - S{Season:D2}E{Episode:D2} - {Title}";
    public string MultiPartSep     { get; set; } = "E";
    public string ExtraTemplate    { get; set; } = "{Series} - S00 - {Title}";
    public bool   SanitizeFileName { get; set; } = true;
}
```

### 8.3 Built-in Templates

| Name | Template String |
|---|---|
| Plex | `{Series}/Season {Season:D2}/{Series} - S{Season:D2}E{Episode:D2} - {Title}` |
| Kodi | `{Series} S{Season:D2}E{Episode:D2} {Title}` |
| Jellyfin | `{Series} - S{Season:D2}E{Episode:D2} - {Title}` |
| FileBot | `{n} - {s00e00} - {t}` |

---

## 9. Linting Subsystem

### 9.1 Interface

```csharp
public interface ILintingEngine
{
    LintReport Lint(EpisodeMap map, LintOptions options);
}

public sealed class LintReport
{
    public IReadOnlyList<LintIssue> Issues { get; init; } = [];
    public bool HasErrors   => Issues.Any(i => i.Severity == LintSeverity.Error);
    public bool HasWarnings => Issues.Any(i => i.Severity == LintSeverity.Warning);
}

public sealed class LintIssue
{
    public required LintSeverity Severity    { get; init; }
    public required string       RuleId      { get; init; }
    public required string       Message     { get; init; }
    public int?                  TrackIndex  { get; init; }
}

public enum LintSeverity { Info, Warning, Error }
```

### 9.2 Built-in Lint Rules

| Rule ID | Severity | Description |
|---|---|---|
| `TV001` | Error | Duplicate episode assignment (two tracks → same SxxExx) |
| `TV002` | Warning | Gap in episode sequence (expected E03 but found E04) |
| `TV003` | Warning | Track duration significantly shorter than TMDB runtime (>25%) |
| `TV004` | Info | Track identified by positional fallback only (Low confidence) |
| `TV005` | Warning | Multi-part episode duration mismatch |
| `TV006` | Error | Season number mismatch between providers |

---

## 10. ArmSharp Extensions Glue

The `ArmMedia.ArmSharpExtensions` project (net10.0) bridges the orchestrator into the existing ARM-Sharp `ArmRipperService`.

```csharp
// Extension method on ArmRipperService
public static async Task<string> PrepareTranscodeInputPathAsync(
    this ArmRipperService ripper,
    DiscContext ctx,
    IEpisodeIdentificationOrchestrator orchestrator,
    IEpisodeRenamer renamer,
    NamingOptions naming,
    CancellationToken ct = default)
{
    var map    = await orchestrator.IdentifyAsync(ctx, ct);
    var track  = map.Tracks.First(t => t.TrackIndex == ctx.CurrentTrackIndex);
    var name   = renamer.Rename(track, naming);
    return Path.Combine(ripper.OutputBasePath, name);
}
```

Dependency wiring (in `ArmSharpServiceCollectionExtensions.cs`):

```csharp
services.AddEpisodeIdentification(configuration)
        .AddProvider<DiscDbProvider>()
        .AddProvider<FileBotProvider>();

services.AddSingleton<IEpisodeRenamer, DefaultEpisodeRenamer>();
services.AddSingleton<ILintingEngine, DefaultLintingEngine>();
```

---

## 11. FileBotProvider Stub

The `FileBotProvider` reads an optional `filebot-map.json` sidecar file dropped alongside the disc image folder. If not found, it returns an empty result set and logs a debug message.

```
filebot-map.json schema:
{
  "discId": "SERIES_S01_D1",
  "mappings": [
    { "trackIndex": 1, "season": 1, "episodes": [1], "title": "Pilot" },
    { "trackIndex": 2, "season": 1, "episodes": [2], "title": "The Setup" },
    { "trackIndex": 3, "season": 1, "episodes": [3, 4], "title": "Part 1 & 2" }
  ]
}
```

---

## 12. Configuration Reference

See `appsettings.example.json` for annotated full config. Key knobs:

| Key | Type | Default | Description |
|---|---|---|---|
| `EpisodeIdentification:ProviderOrder` | string[] | `["DiscDb","FileBot","Tmdb"]` | Provider evaluation order |
| `EpisodeIdentification:ShortCircuitOnDefinitive` | bool | `true` | Stop pipeline on first Definitive result |
| `EpisodeIdentification:RuntimeToleranceSeconds` | int | `180` | Acceptable runtime delta for matching |
| `EpisodeIdentification:MultiPartDurationToleranceSeconds` | int | `300` | Duration delta for multi-part detection |
| `EpisodeIdentification:ExtraMaxDurationSeconds` | int | `600` | Max duration to classify as extra |
| `Naming:Template` | string | Jellyfin pattern | Output filename template |
| `Linting:FailOnError` | bool | `true` | Abort rip if linting errors found |
| `FileBot:MapFilePath` | string | `./filebot-map.json` | Path to optional FileBot sidecar |

---

## 13. Sample Data Shapes

### episode-map.json (output)

```json
{
  "seriesTitle": "Cosmic Frontier",
  "season": 1,
  "generatedAt": "2026-06-29T00:00:00Z",
  "tracks": [
    { "trackIndex": 1, "season": 1, "episodes": [1], "title": "Pilot",           "isExtra": false, "isMultiPart": false, "winningProvider": "DiscDb",   "confidence": 3 },
    { "trackIndex": 2, "season": 1, "episodes": [2,3], "title": "The Long Night", "isExtra": false, "isMultiPart": true,  "winningProvider": "FileBot",  "confidence": 2 },
    { "trackIndex": 4, "season": 0, "episodes": [0],   "title": "Behind Scenes",  "isExtra": true,  "isMultiPart": false, "winningProvider": "DiscDb",   "confidence": 3 }
  ]
}
```

### filebot-map.txt (input sidecar, FileBot native format)

```
/media/disc/track01.mkv --> Cosmic Frontier - S01E01 - Pilot.mkv
/media/disc/track02.mkv --> Cosmic Frontier - S01E02E03 - The Long Night.mkv
/media/disc/track04.mkv --> Cosmic Frontier - S00 - Behind Scenes.mkv
```

---

## 14. Unit Testing Strategy

### Coverage Targets

| Component | Target |
|---|---|
| Orchestrator merge logic | 90% |
| Multi-part detection | 85% |
| Extras detection | 80% |
| FileBotProvider parsing | 95% |
| Naming templates | 90% |
| Lint rules | 80% |

### Key Test Scenarios

1. **DiscDb definitive short-circuit** — DiscDb returns Definitive results; pipeline stops; other providers not called.
2. **Multi-part merge** — Two consecutive tracks whose combined runtime matches a double episode are merged into `episodes: [3,4]`.
3. **Extras detection** — Track < 600 s with no episode match → `isExtra = true`, season → 0.
4. **Provider conflict resolution** — DiscDb says E03 (High), FileBot says E04 (Medium) → DiscDb wins.
5. **Gap detection lint** — Sequence E01, E02, E04 → TV002 warning for E03 missing.
6. **Duplicate episode lint** — Two tracks both map to S01E05 → TV001 error.
7. **FileBotProvider empty** — No `filebot-map.json` present → empty result, no exception.
8. **Runtime tolerance** — Runtime within tolerance → matched; just outside → not matched.

---

## 15. CI / Build

See `.github/workflows/ci.yml` for full details. Summary:

- Matrix build: `net9.0` (shared libs) and `net10.0` (ArmSharpExtensions).
- Test runner: `dotnet test` with `--collect:"XPlat Code Coverage"`.
- Coverage threshold: 80% enforced via `coverlet` + `ReportGenerator`.
- Artifact: NuGet packages uploaded on tag pushes.

---

## 16. Open Issues & Future Work

| ID | Description | Priority |
|---|---|---|
| OI-01 | Add TVDB provider implementation | Medium |
| OI-02 | Add interactive override UI for unmatched tracks | Low |
| OI-03 | Persist EpisodeMap to DiscDb on successful rip | High |
| OI-04 | Streaming/progress reporting from orchestrator | Low |
| OI-05 | Blu-ray disc ID hashing (BD hash vs barcode fallback) | High |
| OI-06 | Cache provider results across rip sessions | Medium |

---

*End of DESIGN-TV-Series-Strategy.md*
