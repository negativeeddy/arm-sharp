# Audit EF Core Migrations — Schema vs Model Consistency

**Priority:** 🟢 Low
**Files:**
- `src/ArmRipper.Core/Migrations/` (all migration files)
- `src/ArmRipper.Core/Infrastructure/Data/ArmDbContext.cs`
- `src/ArmRipper.Core/Models/` (all entity models)

**Status:** ⬜ Todo

---

## Problem

A column mismatch between the EF Core model (`OnModelCreating` + entity properties) and the
actual migration files could cause runtime failures on fresh databases or during migration
from an older version.

Potential issues:
- Property added to model but no migration created
- Column type mismatch (e.g., `TEXT` in migration, `VARCHAR` in model config)
- Index missing that's expected by a query
- FK constraint present in migration but not in model (or vice versa)

## Investigation Tasks

1. Generate a fresh migration in a temp location and diff against existing migrations:
   ```bash
   dotnet ef migrations script --project src/ArmRipper.Core -o /tmp/fresh.sql
   ```
2. Compare the fresh SQL schema with what `DatabaseHelper.EnsureMigrated` produces
3. Check for any properties in entity models that have no corresponding column config
4. Verify all `HasMaxLength`, `HasConversion`, and index configs match the migration
5. Look for any `[NotMapped]` properties that should be mapped (or vice versa)
6. Check for orphaned migration files that were created but never applied

## Deliverable

After deep review, either:
- Close with "schema and model are in sync"
- Create new sub-documents for any migration mismatches found
