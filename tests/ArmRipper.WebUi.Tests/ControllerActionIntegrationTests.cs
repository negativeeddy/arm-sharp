using System.Net;
using System.Text.RegularExpressions;
using ArmRipper.Core.Infrastructure.Data;
using ArmRipper.Core.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using ArmRipper.Core.Configuration;

namespace ArmRipper.WebUi.Tests;

public class ControllerActionIntegrationTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly SqliteConnection _dbConnection;

    public ControllerActionIntegrationTests(WebApplicationFactory<Program> factory)
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

                var hasher = new PasswordHasher<User>();
                db.Users.Add(new User
                {
                    Username = "admin",
                    PasswordHash = hasher.HashPassword(new User(), "admin"),
                    IsAdmin = true
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

    private async Task<HttpClient> CreateAuthenticatedClientAsync()
    {
        var client = _factory.CreateClient();
        var loginPage = await client.GetAsync("/auth/login");
        var html = await loginPage.Content.ReadAsStringAsync();
        var match = Regex.Match(html,
            @"<input[^>]*name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
        var token = match.Success ? match.Groups[1].Value : "";

        var response = await client.PostAsync("/auth/login", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                { "username", "admin" },
                { "password", "admin" },
                { "__RequestVerificationToken", token }
            }));
        response.EnsureSuccessStatusCode();
        return client;
    }

    [Fact]
    public async Task SetTitle_UpdatesJobTitleManual()
    {
        int jobId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ArmDbContext>();
            var job = new Job
            {
                Title = "Original Title",
                TitleAuto = "Original Title",
                Year = "2026",
                VideoType = "movie",
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
            };
            db.Jobs.Add(job);
            await db.SaveChangesAsync();
            jobId = job.Id;
        }

        var client = await CreateAuthenticatedClientAsync();
        var response = await client.PostAsync("/jobs/set-title",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                { "jobId", jobId.ToString() },
                { "title", "New Manual Title" }
            }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ArmDbContext>();
            var job = await db.Jobs.FindAsync(jobId);
            Assert.NotNull(job);
            Assert.Equal("New Manual Title", job.TitleManual);
            Assert.Equal("New Manual Title", job.Title);
        }
    }

    [Fact]
    public async Task SetTitle_MissingJob_ReturnsNotFound()
    {
        var client = await CreateAuthenticatedClientAsync();
        var response = await client.PostAsync("/jobs/set-title",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                { "jobId", "9999" },
                { "title", "Ghost Title" }
            }));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task TitleSearch_ByTitle_ReturnsMatchingResult()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ArmDbContext>();
            db.Jobs.Add(new Job
            {
                Title = "SearchTest Unique Title",
                TitleAuto = "SearchTest Unique Title",
                Year = "2026",
                VideoType = "movie",
                DiscType = DiscType.Dvd,
                Status = JobState.Success,
                StartTime = DateTime.UtcNow,
                DevPath = "/dev/sr99",
                Config = new ConfigSnapshot { MinLength = 300, MaxLength = 9999, RipMethod = "mkv", GetAudioTitle = "" }
            });
            await db.SaveChangesAsync();
        }

        var client = await CreateAuthenticatedClientAsync();
        var response = await client.GetAsync("/jobs/titlesearch?query=SearchTest");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("SearchTest Unique Title", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MarkRead_UpdatesNotificationFlag()
    {
        int notifId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ArmDbContext>();
            var n = new Notification
            {
                EventType = "test_markread",
                Message = "MarkRead test notification",
                Timestamp = DateTime.UtcNow,
                Read = false
            };
            db.Notifications.Add(n);
            await db.SaveChangesAsync();
            notifId = n.Id;
        }

        var client = await CreateAuthenticatedClientAsync();
        var response = await client.PostAsync($"/notifications/markread/{notifId}",
            new FormUrlEncodedContent(new Dictionary<string, string>()));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ArmDbContext>();
            var n = await db.Notifications.FindAsync(notifId);
            Assert.NotNull(n);
            Assert.True(n.Read);
        }
    }

    [Fact]
    public async Task DatabaseDelete_RemovesJob()
    {
        int jobId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ArmDbContext>();
            var job = new Job
            {
                Title = "DeleteTest",
                TitleAuto = "DeleteTest",
                Year = "2026",
                VideoType = "movie",
                DiscType = DiscType.Dvd,
                Status = JobState.Success,
                StartTime = DateTime.UtcNow,
                DevPath = "/dev/sr99",
                Config = new ConfigSnapshot { MinLength = 300, MaxLength = 9999, RipMethod = "mkv", GetAudioTitle = "" }
            };
            db.Jobs.Add(job);
            await db.SaveChangesAsync();
            jobId = job.Id;
        }

        var client = await CreateAuthenticatedClientAsync();
        var response = await client.PostAsync($"/database/delete/{jobId}",
            new FormUrlEncodedContent(new Dictionary<string, string>()));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ArmDbContext>();
            var job = await db.Jobs.FindAsync(jobId);
            Assert.Null(job);
        }
    }

    [Fact]
    public async Task DatabaseDelete_MissingJob_ReturnsNotFound()
    {
        var client = await CreateAuthenticatedClientAsync();
        var response = await client.PostAsync("/database/delete/9999",
            new FormUrlEncodedContent(new Dictionary<string, string>()));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task SaveRipper_ReturnsRedirect()
    {
        var client = await CreateAuthenticatedClientAsync();
        var response = await client.PostAsync("/settings/save-ripper",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                { "MinLength", "600" },
                { "MaxLength", "9999" }
            }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task UpdateIdentification_UpdatesJobMetadata()
    {
        int jobId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ArmDbContext>();
            var job = new Job
            {
                Title = "Old Title",
                TitleAuto = "Old Title",
                Year = "2025",
                VideoType = "series",
                Status = JobState.Active,
                StartTime = DateTime.UtcNow,
                DevPath = "/dev/sr99",
                Config = new ConfigSnapshot { MinLength = 300, MaxLength = 9999, RipMethod = "mkv", GetAudioTitle = "" }
            };
            db.Jobs.Add(job);
            await db.SaveChangesAsync();
            jobId = job.Id;
        }

        var client = await CreateAuthenticatedClientAsync();
        var response = await client.PostAsync("/jobs/update-identification",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                { "jobId", jobId.ToString() },
                { "title", "New Title" },
                { "year", "2026" },
                { "videoType", "movie" }
            }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ArmDbContext>();
            var job = await db.Jobs.FindAsync(jobId);
            Assert.NotNull(job);
            Assert.Equal("New Title", job.TitleManual);
            Assert.Equal("New Title", job.Title);
            Assert.Equal("2026", job.YearManual);
            Assert.Equal("2026", job.Year);
            Assert.Equal("movie", job.VideoTypeManual);
            Assert.Equal("movie", job.VideoType);
        }
    }

    [Fact]
    public async Task UpdateIdentification_MissingJob_ReturnsNotFound()
    {
        var client = await CreateAuthenticatedClientAsync();
        var response = await client.PostAsync("/jobs/update-identification",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                { "jobId", "9999" },
                { "title", "Ghost" }
            }));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ToggleDriveMode_CyclesThroughModes()
    {
        int driveId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ArmDbContext>();
            var drive = new SystemDrive
            {
                Mount = "/dev/sr99",
                Model = "Test Drive",
                DriveMode = "autodetect",
                ReadDvd = true
            };
            db.SystemDrives.Add(drive);
            await db.SaveChangesAsync();
            driveId = drive.Id;
        }

        var client = await CreateAuthenticatedClientAsync();
        var response = await client.PostAsync($"/settings/drive-toggle-mode/{driveId}",
            new FormUrlEncodedContent(new Dictionary<string, string>()));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ArmDbContext>();
            var drive = await db.SystemDrives.FindAsync(driveId);
            Assert.NotNull(drive);
            Assert.Equal("manual", drive.DriveMode);
        }
    }

    [Fact]
    public async Task ToggleDriveMode_MissingDrive_ReturnsNotFound()
    {
        var client = await CreateAuthenticatedClientAsync();
        var response = await client.PostAsync("/settings/drive-toggle-mode/9999",
            new FormUrlEncodedContent(new Dictionary<string, string>()));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task RemoveDrive_RemovesDrive()
    {
        int driveId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ArmDbContext>();
            var drive = new SystemDrive
            {
                Mount = "/dev/sr98",
                Model = "Remove Test Drive",
                DriveMode = "disabled",
                ReadDvd = false
            };
            db.SystemDrives.Add(drive);
            await db.SaveChangesAsync();
            driveId = drive.Id;
        }

        var client = await CreateAuthenticatedClientAsync();
        var response = await client.PostAsync($"/settings/drive-remove/{driveId}",
            new FormUrlEncodedContent(new Dictionary<string, string>()));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ArmDbContext>();
            var drive = await db.SystemDrives.FindAsync(driveId);
            Assert.Null(drive);
        }
    }

    [Fact]
    public async Task RemoveDrive_MissingDrive_ReturnsNotFound()
    {
        var client = await CreateAuthenticatedClientAsync();
        var response = await client.PostAsync("/settings/drive-remove/9999",
            new FormUrlEncodedContent(new Dictionary<string, string>()));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task SaveUi_WithAntiForgeryToken_SavesSettings()
    {
        var client = await CreateAuthenticatedClientAsync();
        var settingsPage = await client.GetAsync("/settings");
        var html = await settingsPage.Content.ReadAsStringAsync();
        var match = Regex.Match(html,
            @"<input[^>]*name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
        var token = match.Success ? match.Groups[1].Value : "";

        var response = await client.PostAsync("/settings/save-ui",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                { "__RequestVerificationToken", token },
                { "theme", "dark" },
                { "refreshRate", "30" },
                { "iconStyle", "filled" }
            }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ArmDbContext>();
            var ui = await db.UiSettings.FirstOrDefaultAsync();
            Assert.NotNull(ui);
            Assert.Equal("dark", ui.Theme);
            Assert.Equal(30, ui.RefreshRate);
            Assert.Equal("filled", ui.IconStyle);
        }
    }

    [Fact]
    public async Task SaveUi_WithoutAntiForgeryToken_ReturnsBadRequest()
    {
        var client = await CreateAuthenticatedClientAsync();
        var response = await client.PostAsync("/settings/save-ui",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                { "theme", "light" },
                { "refreshRate", "15" },
                { "iconStyle", "outline" }
            }));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task MarkAllRead_MarksAllNotificationsAsRead()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ArmDbContext>();
            db.Notifications.Add(new Notification
            {
                EventType = "test",
                Message = "N1",
                Timestamp = DateTime.UtcNow,
                Read = false
            });
            db.Notifications.Add(new Notification
            {
                EventType = "test",
                Message = "N2",
                Timestamp = DateTime.UtcNow,
                Read = false
            });
            await db.SaveChangesAsync();
        }

        var client = await CreateAuthenticatedClientAsync();
        var response = await client.PostAsync("/notifications/markallread",
            new FormUrlEncodedContent(new Dictionary<string, string>()));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ArmDbContext>();
            Assert.Equal(0, await db.Notifications.CountAsync(n => !n.Read));
        }
    }

    [Fact]
    public async Task JobDetail_ExistingJob_ReturnsPage()
    {
        int jobId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ArmDbContext>();
            var job = new Job
            {
                Title = "Detail Test",
                TitleAuto = "Detail Test",
                Year = "2026",
                VideoType = "movie",
                DiscType = DiscType.Dvd,
                Status = JobState.Active,
                StartTime = DateTime.UtcNow,
                DevPath = "/dev/sr99",
                Config = new ConfigSnapshot { MinLength = 300, MaxLength = 9999, RipMethod = "mkv", GetAudioTitle = "" }
            };
            db.Jobs.Add(job);
            await db.SaveChangesAsync();
            jobId = job.Id;
        }

        var client = await CreateAuthenticatedClientAsync();
        var response = await client.GetAsync($"/jobs/jobdetail?jobId={jobId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Detail Test", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task JobDetail_MissingJob_ReturnsNotFound()
    {
        var client = await CreateAuthenticatedClientAsync();
        var response = await client.GetAsync("/jobs/jobdetail?jobId=9999");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ActiveRips_ReturnsPageWithActiveJobs()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ArmDbContext>();
            db.Jobs.Add(new Job
            {
                Title = "Active Rip",
                TitleAuto = "Active Rip",
                Year = "2026",
                VideoType = "movie",
                DiscType = DiscType.Dvd,
                Status = JobState.Active,
                StartTime = DateTime.UtcNow,
                DevPath = "/dev/sr99",
                Config = new ConfigSnapshot { MinLength = 300, MaxLength = 9999, RipMethod = "mkv", GetAudioTitle = "" }
            });
            await db.SaveChangesAsync();
        }

        var client = await CreateAuthenticatedClientAsync();
        var response = await client.GetAsync("/jobs/activerips");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Active Rip", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ActiveRips_WhenNoActiveRips_ReturnsEmptyState()
    {
        var client = await CreateAuthenticatedClientAsync();
        var response = await client.GetAsync("/jobs/activerips");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task DatabaseImport_ReturnsJson()
    {
        var client = await CreateAuthenticatedClientAsync();
        var response = await client.GetAsync("/database/import");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        Assert.Contains("added", json, StringComparison.OrdinalIgnoreCase);
    }
}
