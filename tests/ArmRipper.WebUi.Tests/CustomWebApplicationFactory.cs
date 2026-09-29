using ArmRipper.Core.Configuration;
using ArmRipper.Core.Infrastructure.Data;
using ArmRipper.Core.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ArmRipper.WebUi.Tests;

/// <summary>
/// Shared test factory that creates an isolated SQLite database per test class.
/// Each class that uses <c>IClassFixture&lt;CustomWebApplicationFactory&gt;</c>
/// gets its own instance, eliminating the flakiness caused by parallel DB seeding
/// when multiple test classes share a single <c>WebApplicationFactory&lt;Program&gt;</c>.
///
/// The schema is produced by the real migration chain. Running that chain per factory
/// is expensive (it rebuilds tables for every AlterColumn), so it is built once into a
/// template file that each factory copies. That keeps per-class isolation while paying
/// the migration cost a single time.
/// </summary>
public class CustomWebApplicationFactory : WebApplicationFactory<Program>, IDisposable
{
    private static readonly Lazy<string> TemplatePath = new(
        BuildTemplate, LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly SqliteConnection _dbConnection;
    private readonly string _dbFile;

    public CustomWebApplicationFactory()
    {
        _dbFile = Path.Combine(Path.GetTempPath(), $"arm-webui-tests-{Guid.NewGuid():N}.db");
        File.Copy(TemplatePath.Value, _dbFile);
        _dbConnection = new SqliteConnection($"Data Source={_dbFile}");
        _dbConnection.Open();
    }

    /// <summary>Runs the full migration chain once into a reusable template file.</summary>
    private static string BuildTemplate()
    {
        var path = Path.Combine(Path.GetTempPath(), $"arm-webui-tests-template-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<ArmDbContext>()
            .UseSqlite($"Data Source={path}")
            .Options;
        using (var db = new ArmDbContext(options))
        {
            db.Database.Migrate();
        }

        AppDomain.CurrentDomain.ProcessExit += (_, _) => TryDelete(path);
        return path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // Best effort; a leftover temp file is harmless.
        }
    }

    /// <summary>The underlying SQLite connection, exposed so callers can seed extra data.</summary>
    protected SqliteConnection DbConnection => _dbConnection;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        var webUiDir = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "ArmRipper.WebUi"));
        builder.UseContentRoot(webUiDir);

        builder.ConfigureServices(services =>
        {
            services.PostConfigure<ArmSettings>(a => a.DisableLogin = false);

            var dbDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<ArmDbContext>));
            if (dbDescriptor != null) services.Remove(dbDescriptor);
            services.AddDbContext<ArmDbContext>(options => options.UseSqlite(_dbConnection));

            using var scope = services.BuildServiceProvider().CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ArmDbContext>();
            // The schema already came from the real migration chain via the template
            // copy, so this is a no-op check; the app's own startup runs Migrate() and
            // must find nothing pending.
            db.Database.Migrate();

            SeedDb(db);
        });
    }

    /// <summary>Override to seed additional test data (users, jobs, etc.).</summary>
    protected virtual void SeedDb(ArmDbContext db)
    {
        if (!db.Users.Any())
        {
            var hasher = new PasswordHasher<User>();
            db.Users.Add(new User
            {
                Username = "admin",
                PasswordHash = hasher.HashPassword(new User(), "admin"),
                IsAdmin = true
            });
            db.SaveChanges();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _dbConnection?.Dispose();
            TryDelete(_dbFile);
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// Factory for <see cref="ApiIntegrationTests"/> that seeds the admin user
/// and a test job in a single transaction before any test runs.
/// </summary>
public sealed class ApiTestWebApplicationFactory : CustomWebApplicationFactory
{
    protected override void SeedDb(ArmDbContext db)
    {
        if (!db.Users.Any())
        {
            var hasher = new PasswordHasher<User>();
            db.Users.Add(new User
            {
                Username = "admin",
                PasswordHash = hasher.HashPassword(new User(), "admin"),
                IsAdmin = true
            });
        }

        db.Jobs.Add(new Job
        {
            Title = "Test Movie",
            Year = "2026",
            VideoType = VideoContentType.Movie,
            DiscType = DiscType.Dvd,
            Status = JobState.Active,
            StartTime = DateTime.UtcNow,
            DevPath = "/dev/sr99",
            Config = new ConfigSnapshot
            {
                MinLength = 300,
                MaxLength = 9999,
                RipMethod = "mkv",
                MainFeature = true,
                GetAudioTitle = ""
            }
        });

        db.SaveChanges();
    }
}
