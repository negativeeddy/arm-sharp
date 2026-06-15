using System.Net;
using ArmRipper.Core.Configuration;
using ArmRipper.Core.Infrastructure.Data;
using ArmRipper.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

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

    public static IOptions<ArmSettings> CreateOptions(Action<ArmSettings>? configure = null)
    {
        var s = new ArmSettings();
        configure?.Invoke(s);
        return Options.Create(s);
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
            VideoType = "movie",
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
