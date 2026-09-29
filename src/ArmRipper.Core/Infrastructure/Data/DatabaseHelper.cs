using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArmRipper.Core.Infrastructure.Data;

/// <summary>
/// Shared database initialization used by both the CLI and WebUi entry points.
///
/// Schema changes are made exclusively through EF Core migrations. This type
/// deliberately contains no manual <c>ALTER TABLE</c> patching and no manual
/// <c>__EFMigrationsHistory</c> seeding: that scheme silently diverged from the
/// model, which is how <c>ConfigSnapshot.MakeMkvInfoScanTimeoutMinutes</c>,
/// <c>jobs.DiscVariant</c> and <c>config.ManualSelectionWaitTime</c> ended up in
/// the model with no migration, breaking every query against <c>config</c> on
/// existing databases ("no such column").
///
/// If you add a property to an entity, add a migration for it. The
/// <c>MigrationChain_ProducesSchemaIdenticalToModel</c> test fails CI otherwise.
/// </summary>
public static class DatabaseHelper
{
    /// <summary>
    /// Logger for startup DB-init diagnostics. Callers may assign it before calling
    /// <see cref="EnsureMigrated"/>; defaults to a no-op logger so standalone use still works.
    /// </summary>
    public static ILogger Logger { get; set; } = NullLogger.Instance;

    /// <summary>
    /// Applies all pending EF Core migrations.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when a migration cannot be applied. Failing loudly is deliberate — a
    /// database whose schema does not match the model produces confusing
    /// "no such column" errors much later, so a wrong schema is worse than a
    /// service that refuses to start.
    /// </exception>
    public static void EnsureMigrated(ArmDbContext db)
    {
        try
        {
            db.Database.Migrate();
        }
        catch (Exception ex)
        {
            var message = BuildGuidanceMessage(db);
            Logger.LogError(ex, "{Message}", message);
            throw new InvalidOperationException(message, ex);
        }
    }

    /// <summary>
    /// Builds an operator-facing explanation for a failed <c>Migrate()</c>.
    /// </summary>
    private static string BuildGuidanceMessage(ArmDbContext db)
    {
        var dbPath = db.Database.GetDbConnection().DataSource;
        var pending = SafePendingMigrations(db);
        var pendingText = pending.Count == 0
            ? "unknown"
            : string.Join(", ", pending);

        // Point at the migration that actually failed to apply rather than a
        // hardcoded id, so the hint cannot go stale as migrations are added.
        var skipHint = pending.Count > 0
            ? $"""
                If the error is "duplicate column name", the column already exists and
                only the history row is missing. Record it as applied to skip re-adding:

                  sqlite3 "{dbPath}" "INSERT OR IGNORE INTO __EFMigrationsHistory
                    (MigrationId, ProductVersion)
                    VALUES ('{pending[0]}', '10.0.0');"
                """
            : "Identify the failing migration from the inner exception above.";

        return $"""
            Failed to apply database migrations to '{dbPath}'.

            Pending migration(s): {pendingText}
            (The underlying error is attached as the inner exception.)

            This database was most likely created by a build that changed the schema
            outside the migration system, or by EnsureCreated() rather than Migrate().
            Columns known to have shipped without a migration before the chain was
            repaired: config.ManualSelectionWaitTime, jobs.DiscVariant,
            config.MakeMkvInfoScanTimeoutMinutes.

            {skipHint}

            Otherwise back up the database and report this error.
            """;
    }

    private static List<string> SafePendingMigrations(ArmDbContext db)
    {
        try
        {
            return db.Database.GetPendingMigrations().ToList();
        }
        catch
        {
            // The failure may be precisely what makes this query throw; the original
            // exception is preserved as the inner exception, so don't mask it.
            return new List<string>();
        }
    }

    /// <summary>
    /// Normalizes a SQLite connection string for ARM use.
    ///
    /// <c>busy_timeout</c> used to be applied as a bare <c>PRAGMA</c> in
    /// <see cref="EnsureMigrated"/>, but that pragma is per-connection and does not
    /// survive connection pooling, so most connections got no busy-wait protection
    /// and concurrent writers could fail immediately with SQLITE_BUSY. Putting the
    /// timeout in the connection string makes Microsoft.Data.Sqlite issue it on every
    /// new connection.
    /// </summary>
    public static string AddArmDbConnectionString(string connectionString)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString);

        // Microsoft.Data.Sqlite already defaults DefaultTimeout to 30s, which drives
        // SQLite's busy handler. The old bare `PRAGMA busy_timeout = 5000` actually
        // *lowered* that to 5s, and because the pragma is per-connection it never
        // applied to pooled connections anyway. Enforce a floor instead, and never
        // shorten an explicitly configured value.
        const int MinimumTimeoutSeconds = 5;
        if (builder.DefaultTimeout < MinimumTimeoutSeconds)
            builder.DefaultTimeout = MinimumTimeoutSeconds;

        builder.Pooling = true;
        return builder.ToString();
    }
}
