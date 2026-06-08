using ArmRipper.Core.Configuration;
using ArmRipper.Core.Infrastructure.Data;
using ArmRipper.Core.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ArmRipper.WebUi.Tests;

public class CustomWebApplicationFactory : WebApplicationFactory<Program>, IDisposable
{
    private readonly SqliteConnection _dbConnection;

    public CustomWebApplicationFactory()
    {
        _dbConnection = new SqliteConnection("DataSource=:memory:");
        _dbConnection.Open();
    }

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
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _dbConnection?.Dispose();
        base.Dispose(disposing);
    }
}
