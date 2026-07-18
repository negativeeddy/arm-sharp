# Improvements & Refactoring Notes

Track usability, DX, and architecture improvements. Focus: user-friendliness, easy setup, easy diagnosis.

Each entry below links to a dedicated page in [`docs/improvements/`](./improvements/) with full details.

---

## Gitignore
- [gitignore.md](./improvements/gitignore.md) — DB file `data/arm.db` not gitignored

## Data / Persistence
- [data-persistence.md](./improvements/data-persistence.md) — Migrate from `DateTime` to `DateTimeOffset`

## Configuration & Setup
- [configuration-4k-uhd.md](./improvements/configuration-4k-uhd.md) — Add 4K UHD disc type with separate settings
- [configuration-appsettings.md](./improvements/configuration-appsettings.md) — Environment-aware config profiles
- [configuration-seed-data.md](./improvements/configuration-seed-data.md) — Auto-seed data on first launch

## Disc Type Detection
- [disc-type-audiobook-mp3.md](./improvements/disc-type-audiobook-mp3.md) — MP3 audiobook / audio CD-ROM classification

## UI / User Experience
- [ui-restart-last-stage.md](./improvements/ui-restart-last-stage.md) — Restart from last successful stage

## SignalR
- [signalr.md](./improvements/signalr.md) — Broadcaster lifetime concerns

## Pages / Views
- [pages-identification.md](./improvements/pages-identification.md) — Redesign Identification section
- [pages-disc-detection.md](./improvements/pages-disc-detection.md) — DVD/Blu-ray detection workflow
- [pages-dashboard.md](./improvements/pages-dashboard.md) — Home dashboard enhancements
- [pages-batch-actions.md](./improvements/pages-batch-actions.md) — Batch actions on Active Rips page

## MusicBrainz
- [musicbrainz.md](./improvements/musicbrainz.md) — Investigate moving off XML

## Dependency Injection
- [dependency-injection.md](./improvements/dependency-injection.md) — Review lifetime choices

## Startup & Recovery
- [startup-recovery.md](./improvements/startup-recovery.md) — Resume in-progress rips on restart

## Testing
- [testing.md](./improvements/testing.md) — Deferred / missing tests

## Security
- [security.md](./improvements/security.md) — LogsController sanitization

## MCP (Model Context Protocol)
- [mcp.md](./improvements/mcp.md) — Exposed tools & future tools

## Container / Deployment
- [container-deployment.md](./improvements/container-deployment.md) — Image size, multi-arch, nvdec, buildkit

## Disc Databases (Track Identification)
- [disc-databases.md](./improvements/disc-databases.md) — thediscdb.com integration

## Notifications (Low Priority)
- [notifications-pushover.md](./improvements/notifications-pushover.md) — Pushover integration
- [notifications-apprise.md](./improvements/notifications-apprise.md) — Apprise integration
