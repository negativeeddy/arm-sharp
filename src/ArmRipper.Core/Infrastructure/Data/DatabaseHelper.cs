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
        // ── Idempotent schema patches for columns that may have been added by
        // incomplete migration runs (e.g. when a migration ID changed after a
        // previous attempt partially succeeded). ──
        // Check if PreferWidescreen was already added (by a prior migration attempt
        // with a different ID). If so, mark the current migration as applied so
        // Migrate() won't try to re-add it.
        if (ColumnExists(db, "config", "PreferWidescreen"))
        {
            TryInsertMigration(db, "20260716025640_AddPreferWidescreen");
        }

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
        // MaxConcurrentRips removed — per-drive gating supersedes the global slot.

        // ── ConfigSnapshot columns added after the Initial migration ──
        TryAlterColumn(db, "config", "PreferWidescreen", "INTEGER");

        // ── Seed migration history always ──
        db.Database.ExecuteSql($"INSERT OR IGNORE INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ('20260610044322_Initial', '10.0.0');");
        TryInsertMigration(db, "20260610053400_AddProgressMessage");
        TryInsertMigration(db, "20260610055000_AddManualWaitTime");
        TryInsertMigration(db, "20260612035456_AddDiscTrackFileName");
        TryInsertMigration(db, "20260612040927_AddStageErrors");
        TryInsertMigration(db, "20260613200029_AddManualWaitResume");
        TryInsertMigration(db, "20260614174913_AddCompletedStages");
        TryInsertMigration(db, "20260626033421_AddOriginalJobId");
        TryInsertMigration(db, "20260716025640_AddPreferWidescreen");

        db.Database.ExecuteSql($"PRAGMA busy_timeout = 5000;");
    }

    private static bool ColumnExists(ArmDbContext db, string table, string column)
    {
        try
        {
            var conn = db.Database.GetDbConnection();
            var needClose = conn.State != System.Data.ConnectionState.Open;
            if (needClose) conn.Open();
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}'";
                return (long)(cmd.ExecuteScalar() ?? 0) > 0;
            }
            finally
            {
                if (needClose) conn.Close();
            }
        }
        catch
        {
            return false;
        }
    }

    private static void TryAlterColumn(ArmDbContext db, string table, string column, string? type = null)
    {
        try
        {
            // Check column existence via PRAGMA before attempting ALTER.
            // Use raw ADO.NET — EF Core's SqlQueryRaw wraps the query in a subquery
            // that breaks PRAGMA table_info.
            var conn = db.Database.GetDbConnection();
            var needClose = conn.State != System.Data.ConnectionState.Open;
            if (needClose) conn.Open();
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}'";
                var count = (long)(cmd.ExecuteScalar() ?? 0);
                if (count > 0)
                    return;
            }
            finally
            {
                if (needClose) conn.Close();
            }

            // SQLite doesn't accept parameters in ALTER TABLE DDL.
#pragma warning disable EF1002
            db.Database.ExecuteSqlRaw(
                $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {(type ?? "TEXT")} NULL;");
#pragma warning restore EF1002
        }
        catch { }
    }

    private static void TryInsertMigration(ArmDbContext db, string migrationId)
    {
        try
        {
#pragma warning disable EF1002
            db.Database.ExecuteSqlRaw(
                $"INSERT OR IGNORE INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ('{migrationId}', '10.0.0');");
#pragma warning restore EF1002
        }
        catch { }
    }
}
