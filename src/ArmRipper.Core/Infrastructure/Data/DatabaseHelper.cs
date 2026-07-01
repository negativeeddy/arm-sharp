using Microsoft.EntityFrameworkCore;

namespace ArmRipper.Core.Infrastructure.Data;

/// <summary>
/// Shared database initialization logic used by both CLI and WebUi entry points.
/// Avoids duplicating raw SQL migration-fallback code across projects.
/// </summary>
public static class DatabaseHelper
{
    /// <summary>
    /// Ensures the database is migrated or created. Tries EF Core migrations first;
    /// falls back to EnsureCreated + manual migration-history entries for environments
    /// where migrations haven't been applied (e.g., existing databases from earlier builds).
    /// </summary>
    public static void EnsureMigrated(ArmDbContext db)
    {
        try
        {
            db.Database.Migrate();
        }
        catch
        {
            db.Database.EnsureCreated();
            db.Database.ExecuteSql($"CREATE TABLE IF NOT EXISTS \"__EFMigrationsHistory\" (\"MigrationId\" TEXT NOT NULL, \"ProductVersion\" TEXT NOT NULL);");
        }

        // Idempotent schema patches for migrations that may not have been applied.
        // Run unconditionally — Migrate() may succeed but miss newer columns when
        // the DB predates a migration that was never applied to it.
        TryAlterColumn(db, "jobs", "Warnings");
        TryAlterColumn(db, "jobs", "ProgressMessage");
        TryAlterColumn(db, "jobs", "StageErrors");
        TryAlterColumn(db, "jobs", "ManualWaitResume");
        TryAlterColumn(db, "jobs", "CompletedStages");
        TryAlterColumn(db, "jobs", "OriginalJobId", "INTEGER");
        TryAlterColumn(db, "config", "MaxConcurrentRips");

        // ── Seed migration history always ──
        db.Database.ExecuteSql($"INSERT OR IGNORE INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ('20260610044322_Initial', '10.0.0');");
        TryInsertMigration(db, "20260610053400_AddProgressMessage");
        TryInsertMigration(db, "20260610055000_AddManualWaitTime");
        TryInsertMigration(db, "20260612035456_AddDiscTrackFileName");
        TryInsertMigration(db, "20260612040927_AddStageErrors");
        TryInsertMigration(db, "20260613200029_AddManualWaitResume");
        TryInsertMigration(db, "20260614174913_AddCompletedStages");
        TryInsertMigration(db, "20260626033421_AddOriginalJobId");

        db.Database.ExecuteSql($"PRAGMA busy_timeout = 5000;");
    }

    private static void TryAlterColumn(ArmDbContext db, string table, string column, string? type = null)
    {
        try { db.Database.ExecuteSql($"ALTER TABLE {table} ADD COLUMN {column} {(type ?? "TEXT")} NULL;"); } catch { }
    }

    private static void TryInsertMigration(ArmDbContext db, string migrationId)
    {
        try
        {
            db.Database.ExecuteSql(
                $"INSERT OR IGNORE INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ('{migrationId}', '10.0.0');");
        }
        catch { }
    }
}
