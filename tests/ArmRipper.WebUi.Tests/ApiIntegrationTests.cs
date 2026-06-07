using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using ArmRipper.Core.Infrastructure.Data;
using ArmRipper.Core.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace ArmRipper.WebUi.Tests;

public class ApiIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public ApiIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
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
    public async Task Jobs_ReturnsEmptyArray_WhenNoJobsExist()
    {
        var client = await CreateAuthenticatedClientAsync();
        var response = await client.GetAsync("/api/jobs");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        Assert.Equal("[]", json.Trim());
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
    public async Task Stats_ReturnsZeroCounts_WhenNoJobsExist()
    {
        var client = await CreateAuthenticatedClientAsync();
        var response = await client.GetAsync("/api/stats");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);
        Assert.Equal(0, doc.RootElement.GetProperty("totalJobs").GetInt32());
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
}
