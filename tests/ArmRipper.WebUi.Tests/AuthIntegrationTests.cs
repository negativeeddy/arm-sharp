using System.Net;
using System.Text.RegularExpressions;
using ArmRipper.Core.Infrastructure.Data;
using ArmRipper.Core.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace ArmRipper.WebUi.Tests;

public class AuthIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public AuthIntegrationTests(WebApplicationFactory<Program> factory)
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
    public async Task Login_WithValidCredentials_RedirectsToHome()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var (token, _) = await ExtractAntiForgeryTokenAsync(client, "/auth/login");

        var response = await client.PostAsync("/auth/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            { "username", "admin" },
            { "password", "admin" },
            { "__RequestVerificationToken", token }
        }));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location?.OriginalString ?? "";
        Assert.Equal("/index", location);
    }

    [Fact]
    public async Task Login_WithInvalidPassword_ReturnsLoginPage()
    {
        var client = _factory.CreateClient();
        var (token, _) = await ExtractAntiForgeryTokenAsync(client, "/auth/login");

        var response = await client.PostAsync("/auth/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            { "username", "admin" },
            { "password", "wrongpassword" },
            { "__RequestVerificationToken", token }
        }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Login - ARM", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Login_WithUnknownUser_ReturnsLoginPage()
    {
        var client = _factory.CreateClient();
        var (token, _) = await ExtractAntiForgeryTokenAsync(client, "/auth/login");

        var response = await client.PostAsync("/auth/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            { "username", "nobody" },
            { "password", "anything" },
            { "__RequestVerificationToken", token }
        }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Login - ARM", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Login_WithoutAntiForgeryToken_ReturnsBadRequest()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsync("/auth/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            { "username", "admin" },
            { "password", "admin" }
        }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AuthenticatedUser_CanAccessProtectedPage()
    {
        var client = await CreateAuthenticatedClientAsync();
        var response = await client.GetAsync("/settings");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<(string Token, string Cookie)> ExtractAntiForgeryTokenAsync(HttpClient client, string url)
    {
        var page = await client.GetAsync(url);
        var html = await page.Content.ReadAsStringAsync();
        var match = Regex.Match(html,
            @"<input[^>]*name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
        var token = match.Success ? match.Groups[1].Value : "";

        var cookieHeader = page.Headers.GetValues("Set-Cookie")
            .FirstOrDefault(c => c.Contains(".AspNetCore.Antiforgery", StringComparison.OrdinalIgnoreCase)) ?? "";
        return (token, cookieHeader);
    }

    private async Task<HttpClient> CreateAuthenticatedClientAsync()
    {
        var client = _factory.CreateClient();
        var (token, _) = await ExtractAntiForgeryTokenAsync(client, "/auth/login");

        var response = await client.PostAsync("/auth/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            { "username", "admin" },
            { "password", "admin" },
            { "__RequestVerificationToken", token }
        }));
        response.EnsureSuccessStatusCode();
        return client;
    }
}
