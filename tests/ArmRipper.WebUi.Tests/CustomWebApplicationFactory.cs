using ArmRipper.Core.Configuration;
using ArmRipper.Core.Infrastructure.Data;
using ArmRipper.Core.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ArmRipper.WebUi.Tests;

/// <summary>
/// Shared test factory that creates an isolated in-memory SQLite database per
/// test class. Each class that uses <c>IClassFixture&lt;CustomWebApplicationFactory&gt;</c>
/// gets its own instance, eliminating the flakiness caused by parallel DB seeding
/// when multiple test classes share a single <c>WebApplicationFactory&lt;Program&gt;</c>.
/// </summary>
public class CustomWebApplicationFactory : WebApplicationFactory<Program>, IDisposable
{
    private readonly SqliteConnection _dbConnection;

    public CustomWebApplicationFactory()
    {
        _dbConnection = new SqliteConnection("DataSource=:memory:");
        _dbConnection.Open();
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
            db.Database.EnsureCreated();

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
            _dbConnection?.Dispose();
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
