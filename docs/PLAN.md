# ARM .NET Port — Development Plan

## Strategy
- **Parity-first**: Match all original ARM Web UI features, then test hardware
- **Drop-in Docker**: Container matches ARM's volume/device/port conventions
- **OSS-ready**: Decisions documented, no proprietary deps

## Architecture Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Language | C# .NET 10 | Type-safe, faster than Python, excellent tooling |
| ORM | EF Core + SQLite | Native .NET, LINQ, migrations support |
| Web UI | ASP.NET Core MVC + Razor Pages | Built-in, no JS framework dependency |
| Container base | `arm-dependencies:1.7.3` | Drop-in replacement, all tools pre-installed |
| Auth | Cookie auth + PasswordHasher | Simple, no external dependencies, internal-network trust model |
| Metadata | OMDB + TMDB (same as ARM) | Both APIs remain available |
| Notifications | Apprise-compatible | Retains 30+ notification services from original |

See `ARCHITECTURE.md` for full project structure and pipeline details.

---

## Remaining: Production Compose Drop-In Test

Test `arm-sharp` as drop-in replacement for the original ARM container:

1. Stop original ARM container
2. Point compose `image: arm-sharp` (or `build: .`)
3. Set `command: supervise` in compose
4. Test with DVD/BD inserted
5. Monitor logs + web UI

### Key compose config from existing arm service:
```yaml
environment:
  ARM_UID: "1001"
  ARM_GID: "1001"
  TZ: "America/Chicago"
devices:
  - "/dev/sr0:/dev/sr0"
  - "/dev/sg3:/dev/sg3"
  - "/dev/sr1:/dev/sr1"
  - "/dev/sg4:/dev/sg4"
volumes:
  - /mnt/data/docker/arm/home:/home/arm
  - /mnt/data/docker/arm/logs:/home/arm/logs
  - /mnt/data/docker/arm/media:/home/arm/media
  - /mnt/data/docker/arm/config:/etc/arm/config
  - /mnt/data/media:/home/arm/publish
privileged: true
gpus: all
```

### Potential issues to watch for:
- Web UI first-run sets up admin user — test login flow
- MakeMKV key handling — `EnsureKeyAsync` runs automatically
- Volume mount permissions — `ARM_UID`/`ARM_GID` entrypoint handles this
- No `/dev/sg*` needed for our app (unlike original ARM which uses `discid`)
