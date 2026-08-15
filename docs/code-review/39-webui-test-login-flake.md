# WebUi integration tests flake in `CreateAuthenticatedWithTokenAsync`

**Priority:** 🟢 Low
**File(s):** `tests/ArmRipper.WebUi.Tests/ApiIntegrationTests.cs`
**Status:** ⬜ Todo
**Found in:** PR #72 (mid-rip main-feature redirect)

---

## Problem

Running the four `RedirectRip_*` API tests together, `RedirectRip_MissingJob_ReturnsNotFound`
failed once with an exception inside the login POST of `CreateAuthenticatedWithTokenAsync`
(e.g. the seeded admin user not yet visible to the login handler). The test passed
standalone and on subsequent full-suite runs.

The harness is the likely cause:

- Each test class creates its own in-memory `SqliteConnection`, but all classes
  share one `WebApplicationFactory<Program>` fixture, and xUnit runs test
  classes in parallel. Classes share the app instance, so DB connections
  contend during `EnsureCreated` / seeding.
- `EnsureSeedLoadedAsync` caches the seeded job id in a non-static instance
  field, so it is not shared across tests, but the DB seeding happens in the
  factory's `ConfigureServices` per test instance.

## Proposed Fix

- Add a small retry around the login POST in `CreateAuthenticatedWithTokenAsync`
  (and `CreateAuthenticatedClientAsync`), e.g. retry the whole login+token fetch
  up to 3 times on failure.
- Or isolate the app instances per class with `IClassFixture` on a
  `CustomWebApplicationFactory` (already exists in the test project) that owns
  its own in-memory DB.
