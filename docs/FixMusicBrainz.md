# MusicBrainzService — Fix Plan

Deferred to last. Everything else must be done before touching this.

## Issues

### 1. `new HttpClient()` instead of `IHttpClientFactory`
- **File:** `src/ArmRipper.Core/Rip/MusicBrainzService.cs:73,277`
- **Problem:** Bypasses DI, no connection pooling, socket exhaustion risk under concurrent CD identification. `OmdbService`/`TmdbService` already use `IHttpClientFactory` — follow that pattern.
- **Fix:** Inject `IHttpClientFactory` via constructor, call `clientFactory.CreateClient()`.

### 2. Fire-and-forget `GetCdArtAsync`
- **File:** `src/ArmRipper.Core/Rip/MusicBrainzService.cs:170`
- **Problem:** `_ = GetCdArtAsync(job, disc, ns, ct);` — task is discarded. Exceptions silently swallowed. Artwork result never guaranteed.
- **Fix:** Either await properly, or restructure so artwork fetch is synchronous in the job flow.

### 3. No unit tests — entire service untested
- **File:** `src/ArmRipper.Core/Rip/MusicBrainzService.cs` (308 lines)
- **Untested methods:**
  - `GetDiscIdAsync`
  - `MusicBrainzLookupAsync`
  - `GetDiscInfoAsync`
  - `CheckMusicBrainzData`
  - `ProcessDiscRelease`
  - `ProcessCdStub`
  - `ProcessTracks`
  - `GetCdArtAsync`
- The only tests referencing `IMusicBrainzService` (in `ConductorTests`) mock the entire interface — they test the Conductor, not MusicBrainzService.
- **Fix:** Add dedicated `MusicBrainzServiceTests.cs` with mocked `ICliProcessRunner` for `discid` calls and mocked HTTP responses for MusicBrainz API calls.

### 4. XML parsing fragility

| Line | Issue | Severity |
|------|-------|----------|
| 89 | `XDocument.Parse(xml)` — no schema/DTD validation, throws `XmlException` on malformed input | Medium |
| 93 | `root.GetDefaultNamespace()` — assumes namespace exists; returns empty string if not | Low |
| 167 | `int.Parse(offsetCount)` — unguarded, throws `FormatException`/`OverflowException` | Medium |
| 195 | `int.Parse(trackCount)` — same | Medium |
| 227 | `int.Parse(lengthStr)` — only catches `FormatException`, not `OverflowException` | Low |
| 283 | `doc.RootElement.GetProperty("images")` — assumes property exists, throws `KeyNotFoundException` | Medium |

### 5. Cover Art Archive JSON parsing
- **File:** `src/ArmRipper.Core/Rip/MusicBrainzService.cs:282-293`
- **Problem:** Assumes `images` property always exists in the Cover Art Archive response.
- **Fix:** Use `TryGetProperty` instead of `GetProperty`.

### 6. Other gaps
- XML parsing has no dedicated test coverage (no XML test fixtures)
- `ProcessDiscRelease` returns on first CD match (line 172) — may miss other releases
- No retry logic for MusicBrainz API calls (HTTP 429, transient failures)
- `UserAgent` is a static field — should be per-request configurable
