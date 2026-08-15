using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using ArmRipper.Core.Infrastructure.Data;
using ArmRipper.Core.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using ArmRipper.Core.Configuration;

namespace ArmRipper.WebUi.Tests;

public class ApiIntegrationTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly SqliteConnection _dbConnection;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public ApiIntegrationTests(WebApplicationFactory<Program> factory)
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

                db.Users.Add(new User
                {
                    Username = "admin",
                    PasswordHash = new PasswordHasher<User>().HashPassword(new User(), "admin"),
                    IsAdmin = true
                });

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
            });
        });
    }

    public void Dispose()
    {
        _dbConnection.Close();
        _dbConnection.Dispose();
    }

    private int _testJobId;
    private bool _seedLoaded;

    private async Task EnsureSeedLoadedAsync()
    {
        if (_seedLoaded) return;
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ArmDbContext>();
        _testJobId = (await db.Jobs.FirstAsync<Job>()).Id;
        _seedLoaded = true;
    }

    [Fact]
    public async Task Health_ReturnsJsonWithStatusVersion()
    {
        var client = await CreateAuthenticatedClientAsync();
        var response = await client.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);
        Assert.Equal("healthy", doc.RootElement.GetProperty("status").GetString());
        Assert.True(doc.RootElement.TryGetProperty("version", out _));
        Assert.True(doc.RootElement.TryGetProperty("timestamp", out _));
    }

    [Fact]
    public async Task Jobs_ReturnsSeededJob()
    {
        await EnsureSeedLoadedAsync();
        var client = await CreateAuthenticatedClientAsync();
        var response = await client.GetAsync("/api/jobs");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);
        Assert.Equal(1, doc.RootElement.GetArrayLength());
        Assert.Equal("Test Movie", doc.RootElement[0].GetProperty("title").GetString());
    }

    [Fact]
    public async Task Drives_ReturnsEmptyArray_WhenNoDrivesExist()
    {
        var client = await CreateAuthenticatedClientAsync();
        var response = await client.GetAsync("/api/drives");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        Assert.Equal("[]", json.Trim());
    }

    [Fact]
    public async Task DrivesPartial_RendersMainFeatureOverrideDropdown()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ArmDbContext>();
            db.SystemDrives.Add(new SystemDrive
            {
                SerialId = "SR-PARTIAL-1",
                Mount = "/dev/sr0",
                Model = "Test Drive",
                MainFeature = false // "All" override for this drive
            });
            db.SaveChanges();
        }

        var client = await CreateAuthenticatedClientAsync();
        var response = await client.GetAsync("/api/drives/partial");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("mainFeature", html);
        Assert.Contains("Default (Main)", html); // labels the global default
        Assert.Contains(@"value=""all"" selected", html); // drive override is selected
        Assert.Contains("Rip", html);
    }

    [Fact]
    public async Task Stats_ReturnsCountsMatchingSeededData()
    {
        await EnsureSeedLoadedAsync();
        var client = await CreateAuthenticatedClientAsync();
        var response = await client.GetAsync("/api/stats");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);
        Assert.Equal(1, doc.RootElement.GetProperty("totalJobs").GetInt32());
        Assert.Equal(0, doc.RootElement.GetProperty("successJobs").GetInt32());
        Assert.Equal(0, doc.RootElement.GetProperty("successRate").GetDouble());
    }

    [Fact]
    public async Task GetJobById_ReturnsNotFound_ForMissingJob()
    {
        var client = await CreateAuthenticatedClientAsync();
        var response = await client.GetAsync("/api/jobs/9999");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetJobById_ExistingJob_ReturnsOk()
    {
        await EnsureSeedLoadedAsync();
        var client = await CreateAuthenticatedClientAsync();
        var response = await client.GetAsync($"/api/jobs/{_testJobId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ActiveJobs_ReturnsPartialView()
    {
        await EnsureSeedLoadedAsync();
        var client = await CreateAuthenticatedClientAsync();
        var response = await client.GetAsync("/api/jobs/active");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Test Movie", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ActiveJobs_WhenNoActiveJobs_ReturnsEmptyPartial()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ArmDbContext>();
            foreach (var j in db.Jobs)
                db.Jobs.Remove(j);
            db.SaveChanges();
        }

        var client = await CreateAuthenticatedClientAsync();
        var response = await client.GetAsync("/api/jobs/active");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var html = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Test Movie", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetLog_MissingJob_ReturnsErrorJson()
    {
        var client = await CreateAuthenticatedClientAsync();
        var response = await client.GetAsync("/api/log?jobId=9999");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task GetLog_JobWithoutLogFile_ReturnsErrorJson()
    {
        await EnsureSeedLoadedAsync();
        var client = await CreateAuthenticatedClientAsync();
        var response = await client.GetAsync($"/api/log?jobId={_testJobId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task UnreadCount_ReturnsZero_WhenNoNotifications()
    {
        var client = await CreateAuthenticatedClientAsync();
        var response = await client.GetAsync("/api/notifications/unread");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);
        Assert.Equal(0, doc.RootElement.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task UnreadCount_ReturnsCorrectCount()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ArmDbContext>();
            db.Notifications.Add(new Notification
            {
                EventType = "test",
                Message = "Unread 1",
                Timestamp = DateTime.UtcNow,
                Read = false
            });
            db.Notifications.Add(new Notification
            {
                EventType = "test",
                Message = "Unread 2",
                Timestamp = DateTime.UtcNow,
                Read = false
            });
            db.Notifications.Add(new Notification
            {
                EventType = "test",
                Message = "Read",
                Timestamp = DateTime.UtcNow,
                Read = true
            });
            await db.SaveChangesAsync();
        }

        var client = await CreateAuthenticatedClientAsync();
        var response = await client.GetAsync("/api/notifications/unread");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);
        Assert.Equal(2, doc.RootElement.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Abandon_ExistingJob_ReturnsSuccess()
    {
        await EnsureSeedLoadedAsync();
        var (client, token) = await CreateAuthenticatedWithTokenAsync();
        var response = await client.PostAsync($"/api/abandon/{_testJobId}",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(_testJobId, doc.RootElement.GetProperty("job").GetInt32());
    }

    [Fact]
    public async Task Abandon_MissingJob_ReturnsNotFound()
    {
        var (client, token) = await CreateAuthenticatedWithTokenAsync();
        var response = await client.PostAsync("/api/abandon/9999",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ChangeParams_UpdatesJobConfig()
    {
        await EnsureSeedLoadedAsync();
        var (client, token) = await CreateAuthenticatedWithTokenAsync();
        var form = new Dictionary<string, string>
        {
            { "__RequestVerificationToken", token },
            { "jobId", _testJobId.ToString() },
            { "disctype", "Bluray" },
            { "minLength", "600" },
            { "maxLength", "7200" },
            { "ripMethod", "backup" },
            { "mainFeature", "false" }
        };

        var response = await client.PostAsync("/api/change-params",
            new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ArmDbContext>();
        var job = await db.Jobs.Include(j => j.Config).FirstAsync(j => j.Id == _testJobId);
        Assert.Equal(DiscType.Bluray, job.DiscType);
        Assert.Equal(600, job.Config!.MinLength);
        Assert.Equal(7200, job.Config.MaxLength);
        Assert.Equal("backup", job.Config.RipMethod);
        Assert.False(job.Config.MainFeature);
    }

    [Fact]
    public async Task ChangeParams_MissingJob_ReturnsNotFound()
    {
        var (client, token) = await CreateAuthenticatedWithTokenAsync();
        var form = new Dictionary<string, string>
        {
            { "__RequestVerificationToken", token },
            { "jobId", "9999" }
        };
        var response = await client.PostAsync("/api/change-params",
            new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task RedirectRip_ActiveMainFeatureJob_PersistsOverride()
    {
        await EnsureSeedLoadedAsync();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ArmDbContext>();
            // The startup ShutdownJobCancellationService marks the seeded Active job
            // as Stopping, so restore a ripping state before exercising the endpoint.
            var job = await db.Jobs.Include(j => j.Tracks).FirstAsync(j => j.Id == _testJobId);
            job.Status = JobState.Active;
            job.Tracks.Add(new Track { JobId = _testJobId, TrackNumber = "1", FileName = "title01.mkv", Length = 6000, Process = true });
            job.Tracks.Add(new Track { JobId = _testJobId, TrackNumber = "2", FileName = "title02.mkv", Length = 9000, Process = true });
            await db.SaveChangesAsync();
        }

        var (client, token) = await CreateAuthenticatedWithTokenAsync();
        var form = new Dictionary<string, string>
        {
            { "__RequestVerificationToken", token },
            { "trackNumber", "1" }
        };
        var response = await client.PostAsync($"/api/jobs/{_testJobId}/redirect-rip",
            new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean(), $"Response: {json}");
        Assert.Equal("1", doc.RootElement.GetProperty("track").GetString());
        Assert.False(doc.RootElement.GetProperty("cancelled").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("mainFeatureMode").GetBoolean());

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ArmDbContext>();
            var job = await db.Jobs.FirstAsync(j => j.Id == _testJobId);
            Assert.Equal("1", job.MainFeatureOverrideTrackNumber);
        }
    }

    [Fact]
    public async Task RedirectRip_MissingJob_ReturnsNotFound()
    {
        var (client, token) = await CreateAuthenticatedWithTokenAsync();
        var form = new Dictionary<string, string>
        {
            { "__RequestVerificationToken", token },
            { "trackNumber", "1" }
        };
        var response = await client.PostAsync("/api/jobs/9999/redirect-rip",
            new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task RedirectRip_UnknownTrack_ReturnsError()
    {
        await EnsureSeedLoadedAsync();
        var (client, token) = await CreateAuthenticatedWithTokenAsync();
        var form = new Dictionary<string, string>
        {
            { "__RequestVerificationToken", token },
            { "trackNumber", "999" }
        };
        var response = await client.PostAsync($"/api/jobs/{_testJobId}/redirect-rip",
            new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task RedirectRip_NonRippingJob_ReturnsError()
    {
        await EnsureSeedLoadedAsync();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ArmDbContext>();
            var job = await db.Jobs.FirstAsync(j => j.Id == _testJobId);
            job.Status = JobState.Success;
            await db.SaveChangesAsync();
        }

        var (client, token) = await CreateAuthenticatedWithTokenAsync();
        var form = new Dictionary<string, string>
        {
            { "__RequestVerificationToken", token },
            { "trackNumber", "1" }
        };
        var response = await client.PostAsync($"/api/jobs/{_testJobId}/redirect-rip",
            new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("success").GetBoolean());
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

    private async Task<(HttpClient Client, string Token)> CreateAuthenticatedWithTokenAsync()
    {
        var client = _factory.CreateClient();
        var loginPage = await client.GetAsync("/auth/login");
        var html = await loginPage.Content.ReadAsStringAsync();
        var match = Regex.Match(html,
            @"<input[^>]*name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
        var loginToken = match.Success ? match.Groups[1].Value : "";

        var formData = new Dictionary<string, string>
        {
            { "username", "admin" },
            { "password", "admin" },
            { "__RequestVerificationToken", loginToken }
        };

        var response = await client.PostAsync("/auth/login", new FormUrlEncodedContent(formData));
        response.EnsureSuccessStatusCode();

        // Get an anti-forgery token for API POST requests
        var tokenPage = await client.GetAsync("/database/update");
        var tokenHtml = await tokenPage.Content.ReadAsStringAsync();
        var tokenMatch = Regex.Match(tokenHtml,
            @"<input[^>]*name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
        var apiToken = tokenMatch.Success ? tokenMatch.Groups[1].Value : "";

        return (client, apiToken);
    }
}
