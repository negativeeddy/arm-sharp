using System.Net;
using System.Text.RegularExpressions;
using ArmRipper.Core.Configuration;
using ArmRipper.Core.Infrastructure.Data;
using ArmRipper.Core.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArmRipper.WebUi.Tests;

public class WebUiIntegrationTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly SqliteConnection _dbConnection;
    private bool _disposed;

    public WebUiIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _dbConnection = new SqliteConnection("DataSource=:memory:");
        _dbConnection.Open();

        _factory = factory.WithWebHostBuilder(builder =>
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
        });
    }

    [Fact]
    public async Task Health_Returns200()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task LoginPage_Returns200()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/auth/login");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task HomePage_RedirectsToLogin_WhenUnauthenticated()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var response = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location?.IsAbsoluteUri == true
            ? response.Headers.Location.AbsolutePath
            : response.Headers.Location?.OriginalString ?? "";
        Assert.StartsWith("/auth/login", location, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuthenticatedHomePage_Returns200()
    {
        var client = await CreateAuthenticatedClientAsync();
        var response = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task JobsPage_Returns200_WhenAuthenticated()
    {
        var client = await CreateAuthenticatedClientAsync();
        var response = await client.GetAsync("/jobs/titlesearch");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task HistoryPage_Returns200_WhenAuthenticated()
    {
        var client = await CreateAuthenticatedClientAsync();
        var response = await client.GetAsync("/history");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task LogsPage_Returns200_WhenAuthenticated()
    {
        var client = await CreateAuthenticatedClientAsync();
        var response = await client.GetAsync("/logs");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task DatabasePage_Returns200_WhenAuthenticated()
    {
        var client = await CreateAuthenticatedClientAsync();
        var response = await client.GetAsync("/database");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task DatabaseUpdatePage_Returns200_WhenAuthenticated()
    {
        var client = await CreateAuthenticatedClientAsync();
        var response = await client.GetAsync("/database/update");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task SettingsPage_Returns200_WhenAuthenticated()
    {
        var client = await CreateAuthenticatedClientAsync();
        var response = await client.GetAsync("/settings");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task NotificationsPage_Returns200_WhenAuthenticated()
    {
        var client = await CreateAuthenticatedClientAsync();
        var response = await client.GetAsync("/notifications");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ErrorPage_Returns200_WithoutAuth()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/error?message=test");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task SetupPage_Returns200_WithoutAuth()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/setup");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Logout_RedirectsToLogin()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var response = await client.GetAsync("/auth/logout");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location?.IsAbsoluteUri == true
            ? response.Headers.Location.AbsolutePath
            : response.Headers.Location?.OriginalString ?? "";
        Assert.StartsWith("/auth/login", location, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AllAuthenticatedPages_RequireAuth()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var paths = new[] { "/", "/jobs/titlesearch", "/history", "/logs", "/database", "/settings", "/notifications" };

        foreach (var path in paths)
        {
            var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            var location = response.Headers.Location?.IsAbsoluteUri == true
            ? response.Headers.Location.AbsolutePath
            : response.Headers.Location?.OriginalString ?? "";
        Assert.StartsWith("/auth/login", location, StringComparison.Ordinal);
        }
    }

    private async Task<HttpClient> CreateAuthenticatedClientAsync()
    {
        var client = _factory.CreateClient();

        var loginPage = await client.GetAsync("/auth/login");
        var html = await loginPage.Content.ReadAsStringAsync();
        var match = Regex.Match(html,
            @"<input[^>]*name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
        var token = match.Success ? match.Groups[1].Value : "";

        var formData = new Dictionary<string, string>
        {
            { "username", "admin" },
            { "password", "admin" },
            { "__RequestVerificationToken", token }
        };

        var response = await client.PostAsync("/auth/login", new FormUrlEncodedContent(formData));
        response.EnsureSuccessStatusCode();

        return client;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _dbConnection?.Dispose();
            _disposed = true;
        }
    }
}
