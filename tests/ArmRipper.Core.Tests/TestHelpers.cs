using System.Net;
using ArmRipper.Core.Configuration;
using ArmRipper.Core.Infrastructure.Data;
using ArmRipper.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Moq;

namespace ArmRipper.Core.Tests;

public static class TestHelpers
{
    public static ArmDbContext CreateDbContext()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<ArmDbContext>()
            .UseSqlite(connection)
            .Options;
        var ctx = new ArmDbContext(options);
        ctx.Database.EnsureCreated();
        return ctx;
    }

    /// <summary>
    /// Builds test settings whose media and log paths point at a per-call temp root
    /// instead of the real <c>/home/arm</c> tree. Code under test creates directories and
    /// writes rip output, so leaving <see cref="ArmSettings"/>' production defaults in place
    /// leaks test fixtures into the user's actual library. The temp paths are applied first
    /// so <paramref name="configure"/> can still override any individual setting.
    /// </summary>
    public static IOptions<ArmSettings> CreateOptions(Action<ArmSettings>? configure = null)
    {
        var tmpRoot = Path.Combine(Path.GetTempPath(), "arm-test", Guid.NewGuid().ToString());
        var s = new ArmSettings
        {
            RawPath = Path.Combine(tmpRoot, "raw"),
            TranscodePath = Path.Combine(tmpRoot, "transcode"),
            CompletedPath = Path.Combine(tmpRoot, "completed"),
            LogPath = Path.Combine(tmpRoot, "logs"),
        };
        configure?.Invoke(s);
        return Options.Create(s);
    }

    /// <summary>
    /// Returns an <see cref="ISettingsService"/> whose effective settings come from
    /// <paramref name="options"/> (file defaults). Unit tests that don't seed DB
    /// overrides get a service returning the same defaults the code under test expects.
    /// </summary>
    public static ISettingsService CreateSettingsService(IOptions<ArmSettings>? options = null)
    {
        var opts = options ?? CreateOptions();
        var mock = new Mock<ISettingsService>();
        mock.Setup(s => s.GetEffectiveAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(opts.Value);
        return mock.Object;
    }

    public static Job CreateTestJob(Action<Job>? configure = null, Action<ConfigSnapshot>? configureConfig = null)
    {
        var config = new ConfigSnapshot();
        configureConfig?.Invoke(config);

        var job = new Job
        {
            Id = 1,
            DevPath = "/dev/sr0",
            MountPoint = "/mnt/disc",
            Title = "Test Movie",
            TitleAuto = "Test Movie",
            Year = "2024",
            VideoType = VideoContentType.Movie,
            DiscType = DiscType.Dvd,
            Status = JobState.Active,
            Config = config,
            HasNiceTitle = true,
            Label = "TEST_MOVIE",
            StartTime = DateTime.UtcNow
        };
        configure?.Invoke(job);
        return job;
    }

    public static HttpClient CreateMockHttpClient(string response, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage
        {
            StatusCode = statusCode,
            Content = new StringContent(response)
        });
        return new HttpClient(handler);
    }

    public static HttpClient CreateMockHttpClient(Func<HttpRequestMessage, HttpResponseMessage> handlerFunc)
    {
        var handler = new FakeHttpMessageHandler(handlerFunc);
        return new HttpClient(handler);
    }

    private sealed class FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handlerFunc) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            return Task.FromResult(handlerFunc(request));
        }
    }
}
