# Data / Persistence — Migrate from `DateTime` to `DateTimeOffset`

All model timestamps (`Job.StartTime`, `Job.StopTime`, `Notification.Timestamp`, `DiscMetadata.CreatedAt`/`LastUsedAt`, etc.) use `DateTime`. EF Core reads these back as `DateTimeKind.Unspecified` from SQLite, losing the UTC context. `.ToLocalTime()` in views works but is a band-aid.

Switching to `DateTimeOffset` stores the offset with the value and makes timezone intent explicit. Requires touching models, EF mappings, all view comparisons, and serialization.
