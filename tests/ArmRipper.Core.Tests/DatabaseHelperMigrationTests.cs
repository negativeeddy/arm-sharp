using ArmRipper.Core.Configuration;
using ArmRipper.Core.Infrastructure.Data;
using ArmRipper.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ArmRipper.Core.Tests;

/// <summary>
/// Guards the EF Core migration chain against drift from the model.
///
/// This suite replaces the old <c>DatabaseHelper.TryAlterColumn</c> patch scheme.
/// Those patches existed because nothing caught entity properties that shipped
/// without a migration. Three did: <c>config.ManualSelectionWaitTime</c> (13f415e,
/// which also lied to the model snapshot), <c>jobs.DiscVariant</c> (e6b6d0d) and
/// <c>config.MakeMkvInfoScanTimeoutMinutes</c> (d6ca943). All three broke every
/// query against <c>config</c> on existing databases with "no such column".
/// </summary>
public sealed class DatabaseHelperMigrationTests
{
    private static SqliteConnection FreshConnection()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        return connection;
    }

    private static DbContextOptions<ArmDbContext> OptionsFor(SqliteConnection connection) =>
        new DbContextOptionsBuilder<ArmDbContext>().UseSqlite(connection).Options;

    private static Dictionary<string, Dictionary<string, string>> ReadSchema(SqliteConnection connection)
    {
        // Column name -> "type nullability", so that nullability is compared too. A
        // missing column is one failure mode; a column that exists but is nullable
        // when the model is not is another (it reads back NULL into a non-nullable
        // CLR property and throws at runtime).
        var schema = new Dictionary<string, Dictionary<string, string>>();

        var tableNames = new List<string>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText =
                "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'";
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) tableNames.Add(reader.GetString(0));
        }

        foreach (var table in tableNames)
        {
            var columns = new Dictionary<string, string>();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT name, type, \"notnull\", dflt_value FROM pragma_table_info('{table}')";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var notNull = reader.GetInt64(2) != 0 ? "NOT NULL" : "NULL";
                // The SQL-level DEFAULT is deliberately excluded from comparison:
                // EnsureCreated() omits DEFAULT clauses while migrations include them,
                // so the two paths always differ there. Defaults are supplied by the
                // C# model and only matter for rows written outside EF. Type and
                // nullability are what actually break reads, so those are compared.
                columns[reader.GetString(0)] = $"{reader.GetString(1)} {notNull}";
            }
            schema[table] = columns;
        }

        return schema;
    }

    [Fact]
    public void MigrationChain_ProducesSchemaIdenticalToModel()
    {
        // The guard that makes the patch scheme unnecessary. Any entity property
        // added without a migration shows up here as a missing or mis-typed column.
        using var migrated = FreshConnection();
        using (var db = new ArmDbContext(OptionsFor(migrated)))
        {
            db.Database.Migrate();
        }

        using var fromModel = FreshConnection();
        using (var db = new ArmDbContext(OptionsFor(fromModel)))
        {
            db.Database.EnsureCreated();
        }

        var migrationSchema = ReadSchema(migrated);
        var modelSchema = ReadSchema(fromModel);

        // EF's own bookkeeping tables are expected to differ.
        foreach (var table in new[] { "__EFMigrationsHistory", "__EFMigrationsLock" })
        {
            migrationSchema.Remove(table);
            modelSchema.Remove(table);
        }

        var problems = new List<string>();
        foreach (var (table, expectedColumns) in modelSchema)
        {
            if (!migrationSchema.TryGetValue(table, out var actualColumns))
            {
                problems.Add($"migration chain never creates table '{table}'");
                continue;
            }

            foreach (var (column, expectedShape) in expectedColumns)
            {
                if (!actualColumns.TryGetValue(column, out var actualShape))
                {
                    problems.Add($"no migration creates {table}.{column}");
                }
                else if (actualShape != expectedShape)
                {
                    problems.Add(
                        $"{table}.{column} differs — migrations produce '{actualShape}', " +
                        $"model requires '{expectedShape}'");
                }
            }
        }

        Assert.True(
            problems.Count == 0,
            "The migration chain does not match the model. Add or fix a migration for:" +
            Environment.NewLine + string.Join(Environment.NewLine, problems.Select(p => "  - " + p)));
    }

    [Fact]
    public void Migrate_LeavesNoPendingMigrations()
    {
        using var connection = FreshConnection();
        using var db = new ArmDbContext(OptionsFor(connection));

        db.Database.Migrate();

        var pending = db.Database.GetPendingMigrations().ToList();
        Assert.Empty(pending);
    }

    [Fact]
    public async Task EnsureMigrated_CreatesUsableConfigColumns()
    {
        // The originally reported crash: selecting the column through EF against a
        // database that had never been migrated to the current model.
        using var connection = FreshConnection();
        await using var db = new ArmDbContext(OptionsFor(connection));

        DatabaseHelper.EnsureMigrated(db);

        var job = new Job
        {
            DevPath = "/dev/sr0",
            Status = JobState.Active,
            StartTime = DateTime.UtcNow,
            DiscVariant = "B"
        };
        job.Config = ConfigSnapshot.FromSettings(
            new ArmSettings
            {
                MakeMkvInfoScanTimeoutMinutes = 15,
                ManualSelectionWaitTime = 42
            },
            jobId: 0);
        db.Jobs.Add(job);
        job.Config.JobId = job.Id;
        db.ConfigSnapshots.Add(job.Config);
        await db.SaveChangesAsync();

        await using var readBack = new ArmDbContext(OptionsFor(connection));
        var loaded = await readBack.ConfigSnapshots.AsNoTracking().FirstAsync();
        Assert.Equal(15, loaded.MakeMkvInfoScanTimeoutMinutes);
        Assert.Equal(42, loaded.ManualSelectionWaitTime);

        var reloadedJob = await readBack.Jobs.AsNoTracking().FirstAsync(j => j.DevPath == "/dev/sr0");
        Assert.Equal("B", reloadedJob.DiscVariant);
    }

    [Fact]
    public void EnsureMigrated_ThrowsWithDuplicateColumnGuidance_WhenColumnExistsWithoutHistory()
    {
        // The "patch-era" database: a build that added these columns via the old
        // DatabaseHelper.TryAlterColumn scheme recorded no migration history, so
        // Migrate() now tries to add a column that already exists. There is no
        // reconciliation for this by design — it must fail loudly and say how to fix it.
        using var connection = FreshConnection();
        using (var setup = new ArmDbContext(OptionsFor(connection)))
        {
            setup.Database.Migrate();

            // Column stays; history row is rolled back to the pre-change state.
            setup.Database.ExecuteSqlRaw(
                "DELETE FROM \"__EFMigrationsHistory\" WHERE MigrationId LIKE '%AddMissedSnapshotColumns'");
        }

        using var db = new ArmDbContext(OptionsFor(connection));
        var ex = Assert.Throws<InvalidOperationException>(() => DatabaseHelper.EnsureMigrated(db));

        Assert.Contains("Failed to apply database migrations", ex.Message);
        Assert.Contains("duplicate column name", ex.Message, StringComparison.OrdinalIgnoreCase);
        // The remediation must be spelled out, including the exact history row to insert.
        Assert.Contains("AddMissedSnapshotColumns", ex.Message);
        Assert.Contains("__EFMigrationsHistory", ex.Message);
    }

    [Fact]
    public void EnsureMigrated_ThrowsWithActionableGuidance_WhenMigrationCannotApply()
    {
        // Fails loudly rather than swallowing: the old catch-all turned a failed
        // migration into "no such column" errors much later, far from the cause.
        using var connection = FreshConnection();
        using var db = new ArmDbContext(OptionsFor(connection));

        // A table that already exists but no migration history: the signature of a
        // database created by EnsureCreated() instead of Migrate().
        using (var setup = new ArmDbContext(OptionsFor(connection)))
        {
            setup.Database.EnsureCreated();
        }

        var ex = Assert.Throws<InvalidOperationException>(() => DatabaseHelper.EnsureMigrated(db));

        Assert.Contains("Failed to apply database migrations", ex.Message);
        Assert.Contains(connection.DataSource ?? string.Empty, ex.Message);
        Assert.NotNull(ex.InnerException);
    }

    [Fact]
    public void EnsureMigrated_IsIdempotentAcrossRepeatedStartup()
    {
        using var connection = FreshConnection();

        for (var i = 0; i < 3; i++)
        {
            using var db = new ArmDbContext(OptionsFor(connection));
            DatabaseHelper.EnsureMigrated(db);
            Assert.Empty(db.Database.GetPendingMigrations());
        }
    }

    [Fact]
    public void AddArmDbConnectionString_PreservesDataSourceAndSetsBusyTimeout()
    {
        var result = DatabaseHelper.AddArmDbConnectionString("Data Source=/etc/arm/config/arm-sharp.db");

        var parsed = new SqliteConnectionStringBuilder(result);
        Assert.Equal("/etc/arm/config/arm-sharp.db", parsed.DataSource);
        Assert.True(parsed.DefaultTimeout >= 5);
    }
}
