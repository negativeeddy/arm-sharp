using System.Net;
using System.Text.RegularExpressions;
using ArmRipper.Core.Infrastructure.Data;
using ArmRipper.Core.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

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
                if (!db.Users.Any(u => u.Username == "admin"))
                {
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
    public async Task TitleSearch_NoQuery_ShowsSearchForm()
    {
        var client = await CreateAuthenticatedClientAsync();
        var response = await client.GetAsync("/jobs/titlesearch");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Search Movies", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TitleSearch_WithQuery_ShowsNoResultsWhenNoApiKey()
    {
        var client = await CreateAuthenticatedClientAsync();
        var response = await client.GetAsync("/jobs/titlesearch?query=SearchTest");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("No results found", html, StringComparison.OrdinalIgnoreCase);
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
    public async Task SaveRipper_SavesCorrectFields()
    {
        var client = await CreateAuthenticatedClientAsync();
        var response = await client.PostAsync("/settings/save-ripper",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                { "RipMethod", "backup" },
                { "MkvArgs", "--decrypt" },
                { "MinLength", "300" },
                { "MaxLength", "5000" },
                { "MainFeature", "true" },
                { "AutoEject", "false" }
            }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ArmDbContext>();
            var settings = await db.RipperSettings.FirstOrDefaultAsync();
            Assert.NotNull(settings);
            var dict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(settings.SettingsJson);
            Assert.NotNull(dict);

            Assert.Equal("backup", dict["RipMethod"].GetString());
            Assert.Equal("--decrypt", dict["MkvArgs"].GetString());
            Assert.Equal(300, dict["MinLength"].GetInt32());
            Assert.Equal(5000, dict["MaxLength"].GetInt32());
            Assert.True(dict["MainFeature"].GetBoolean());
            Assert.False(dict["AutoEject"].GetBoolean());
        }
    }

    [Fact]
    public async Task SaveRipper_DoesNotOverwriteTranscodeFields()
    {
        var client = await CreateAuthenticatedClientAsync();

        // First, save some transcode values
        var transcodeResponse = await client.PostAsync("/settings/save-transcode",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                { "SkipTranscode", "true" },
                { "DelRawFiles", "true" },
                { "DestExt", "mp4" },
                { "MaxConcurrentTranscodes", "3" }
            }));
        Assert.Equal(HttpStatusCode.OK, transcodeResponse.StatusCode);

        // Now save ripper fields
        var ripperResponse = await client.PostAsync("/settings/save-ripper",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                { "RipMethod", "mkv" },
                { "MinLength", "600" },
                { "MaxLength", "99999" },
                { "MainFeature", "true" },
                { "AutoEject", "true" }
            }));
        Assert.Equal(HttpStatusCode.OK, ripperResponse.StatusCode);

        // Verify transcode fields were NOT overwritten
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ArmDbContext>();
            var settings = await db.RipperSettings.FirstOrDefaultAsync();
            Assert.NotNull(settings);
            var dict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(settings.SettingsJson);
            Assert.NotNull(dict);

            // Transcode fields should still be there
            Assert.True(dict["SkipTranscode"].GetBoolean(), "SkipTranscode should still be true");
            Assert.True(dict["DelRawFiles"].GetBoolean(), "DelRawFiles should still be true");
            Assert.Equal("mp4", dict["DestExt"].GetString());
            Assert.Equal(3, dict["MaxConcurrentTranscodes"].GetInt32());

            // Ripper fields should be updated
            Assert.Equal("mkv", dict["RipMethod"].GetString());
            Assert.Equal(600, dict["MinLength"].GetInt32());
        }
    }

    [Fact]
    public async Task SaveTranscode_SavesCorrectFields()
    {
        var client = await CreateAuthenticatedClientAsync();
        var response = await client.PostAsync("/settings/save-transcode",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                { "SkipTranscode", "false" },
                { "UseFfmpeg", "true" },
                { "DelRawFiles", "true" },
                { "DestExt", "mp4" },
                { "MaxConcurrentTranscodes", "2" },
                { "HbPresetDvd", "Fast 1080p30" },
                { "HbPresetBd", "Fast 1080p30" },
                { "HbArgsDvd", "--quality 20" },
                { "FfmpegCli", "/usr/bin/ffmpeg" }
            }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ArmDbContext>();
            var settings = await db.RipperSettings.FirstOrDefaultAsync();
            Assert.NotNull(settings);
            var dict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(settings.SettingsJson);
            Assert.NotNull(dict);

            Assert.False(dict["SkipTranscode"].GetBoolean());
            Assert.True(dict["UseFfmpeg"].GetBoolean());
            Assert.True(dict["DelRawFiles"].GetBoolean());
            Assert.Equal("mp4", dict["DestExt"].GetString());
            Assert.Equal(2, dict["MaxConcurrentTranscodes"].GetInt32());
            Assert.Equal("Fast 1080p30", dict["HbPresetDvd"].GetString());
            Assert.Equal("Fast 1080p30", dict["HbPresetBd"].GetString());
            Assert.Equal("--quality 20", dict["HbArgsDvd"].GetString());
            Assert.Equal("/usr/bin/ffmpeg", dict["FfmpegCli"].GetString());
        }
    }

    [Fact]
    public async Task SaveTranscode_DoesNotOverwriteRipperFields()
    {
        var client = await CreateAuthenticatedClientAsync();

        // First, save some ripper values
        var ripperResponse = await client.PostAsync("/settings/save-ripper",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                { "RipMethod", "abcde" },
                { "MkvArgs", "--test" },
                { "MinLength", "400" },
                { "MaxLength", "8000" },
                { "MainFeature", "false" },
                { "AutoEject", "false" }
            }));
        Assert.Equal(HttpStatusCode.OK, ripperResponse.StatusCode);

        // Now save transcode fields
        var transcodeResponse = await client.PostAsync("/settings/save-transcode",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                { "SkipTranscode", "true" },
                { "DelRawFiles", "true" }
            }));
        Assert.Equal(HttpStatusCode.OK, transcodeResponse.StatusCode);

        // Verify ripper fields were NOT overwritten
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ArmDbContext>();
            var settings = await db.RipperSettings.FirstOrDefaultAsync();
            Assert.NotNull(settings);
            var dict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(settings.SettingsJson);
            Assert.NotNull(dict);

            // Ripper fields should still be there
            Assert.Equal("abcde", dict["RipMethod"].GetString());
            Assert.Equal("--test", dict["MkvArgs"].GetString());
            Assert.Equal(400, dict["MinLength"].GetInt32());
            Assert.Equal(8000, dict["MaxLength"].GetInt32());
            Assert.False(dict["MainFeature"].GetBoolean());
            Assert.False(dict["AutoEject"].GetBoolean());

            // Transcode fields should be updated
            Assert.True(dict["SkipTranscode"].GetBoolean());
            Assert.True(dict["DelRawFiles"].GetBoolean());
        }
    }

    [Fact]
    public async Task SaveRipper_ReturnsCorrectTab()
    {
        var client = await CreateAuthenticatedClientAsync();
        var response = await client.PostAsync("/settings/save-ripper",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                { "RipMethod", "mkv" },
                { "MinLength", "600" }
            }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Verify the page renders with tab3 active
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("id=\"tab3-tab\"", html);
    }

    [Fact]
    public async Task SaveTranscode_ReturnsCorrectTab()
    {
        var client = await CreateAuthenticatedClientAsync();
        var response = await client.PostAsync("/settings/save-transcode",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                { "SkipTranscode", "true" }
            }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Verify the page renders with tab8 active
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("id=\"tab8-tab\"", html);
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

    [Fact]
    public async Task FixMovieDir_MovesFileToMoviesSubfolder()
    {
        // Arrange — create a temp file in a "wrong" folder under the completed path
        var tempDir = Path.Combine(Path.GetTempPath(), "armtest", Guid.NewGuid().ToString());
        var completedPath = Path.Combine(tempDir, "completed");
        var sourceDir = Path.Combine(completedPath, "movieA");
        var sourceFile = Path.Combine(sourceDir, "movieA.mkv");
        Directory.CreateDirectory(sourceDir);
        await File.WriteAllTextAsync(sourceFile, "dummy content");

        // Create a client with CompletedPath pointing to our temp dir
        // Do NOT follow redirects so we can see the 302 from RedirectToAction
        var client = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.PostConfigure<ArmSettings>(a =>
                {
                    a.CompletedPath = completedPath;
                    a.DisableLogin = false;
                });
            });
        }).CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        // Login
        var loginPage = await client.GetAsync("/auth/login");
        var loginHtml = await loginPage.Content.ReadAsStringAsync();
        var tokenMatch = Regex.Match(loginHtml,
            @"<input[^>]*name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
        var token = tokenMatch.Success ? tokenMatch.Groups[1].Value : "";

        var loginResponse = await client.PostAsync("/auth/login", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                { "username", "admin" },
                { "password", "admin" },
                { "__RequestVerificationToken", token }
            }));
        // Login redirects on success (302), which is expected
        Assert.Equal(HttpStatusCode.Redirect, loginResponse.StatusCode);

        // Act
        var response = await client.PostAsync("/completed/fix-movie-dir",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                { "filePath", sourceFile },
                { "movieTitle", "movieA" }
            }));

        // Assert
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        var expectedFile = Path.Combine(completedPath, "movies", "movieA", "movieA.mkv");
        Assert.False(File.Exists(sourceFile), "Source file should have been moved");
        Assert.True(File.Exists(expectedFile), $"Expected file at {expectedFile}");

        // Cleanup
        try { Directory.Delete(tempDir, true); } catch { }
    }
}
