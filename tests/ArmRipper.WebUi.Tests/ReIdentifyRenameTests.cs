using System.Text.Json;
using ArmMedia.Core.Abstractions;
using ArmMedia.Core.Models;
using ArmRipper.Core.Infrastructure.Data;
using ArmRipper.Core.Models;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArmRipper.WebUi.Tests;

/// <summary>
/// Integration tests for the re-identify rename flow, specifically the two-phase
/// (temp-staging) rename that handles episode-number shift chains where each
/// destination is itself a source of another rename in the same set.
/// </summary>
public class ReIdentifyRenameTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public ReIdentifyRenameTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Run_ShiftChain_RenamesAllFilesInsteadOfSkipping()
    {
        // Old state: 9 tracks identified as E07..E15, files on disk at those paths.
        // New identification shifts the whole chain to E06..E14. With single-file
        // renames this would fail (E07→E06 fails first, etc.). The two-phase
        // staging must rename every file with status "renamed".
        var tempBase = Path.Combine(Path.GetTempPath(), "armtest", Guid.NewGuid().ToString());
        var completedBase = Path.Combine(tempBase, "completed");
        const string series = "Shift Chain Series";
        var seasonDir = Path.Combine(completedBase, "tv", series, "Season 01");

        int jobId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ArmDbContext>();
            var job = new Job
            {
                Title = series,
                Year = "2026",
                VideoType = VideoContentType.Series,
                DiscType = DiscType.Dvd,
                Status = JobState.Success,
                StartTime = DateTime.UtcNow,
                StopTime = DateTime.UtcNow,
                DevPath = "/dev/sr99",
                SeasonNumber = 1,
                Config = new ConfigSnapshot
                {
                    CompletedPath = completedBase,
                    DestExt = "mkv",
                    MinLength = 300,
                    MaxLength = 9999,
                    RipMethod = "mkv",
                    GetAudioTitle = ""
                }
            };

            for (var i = 0; i < 9; i++)
            {
                job.Tracks.Add(new Track
                {
                    TrackNumber = i.ToString(),
                    FileName = $"title{i.ToString("D2")}.mkv",
                    Length = 1200 + i * 100,
                    Ripped = true,
                    Process = true,
                    EpisodeNumber = 7 + i,
                    EpisodeTitle = $"Old Episode {7 + i}",
                    TrackSeasonNumber = 1
                });
            }

            db.Jobs.Add(job);
            await db.SaveChangesAsync();
            jobId = job.Id;
        }

        // Place the old files on disk at the expected SxxExx paths.
        Directory.CreateDirectory(seasonDir);
        var oldPaths = new List<string>();
        for (var i = 0; i < 9; i++)
        {
            var oldPath = Path.Combine(seasonDir, $"S01E{7 + i:D2} - Old Episode {7 + i}.mkv");
            await File.WriteAllTextAsync(oldPath, $"fake-content-{i}");
            oldPaths.Add(oldPath);
        }

        // Fake orchestrator returns the shifted map E07..E15 -> E06..E14.
        var shiftedMap = new EpisodeMap
        {
            SeriesTitle = series,
            Season = 1,
            Tracks = Enumerable.Range(0, 9)
                .Select(i => new MappedTrack
                {
                    TrackIndex = i,
                    Season = 1,
                    Episodes = new[] { 6 + i },
                    Title = $"Old Episode {7 + i}",
                    WinningProvider = "test",
                    Confidence = Confidence.Definitive
                })
                .ToList()
        };

        try
        {
            var client = _factory
                .WithWebHostBuilder(builder => builder.ConfigureServices(services =>
                {
                    services.AddSingleton<IEpisodeIdentificationOrchestrator>(new FakeOrchestrator(shiftedMap));
                }))
                .CreateClient();

            // Login (mirrors ControllerActionIntegrationTests help flow).
            var loginPage = await client.GetAsync("/auth/login");
            var loginHtml = await loginPage.Content.ReadAsStringAsync();
            var token = System.Text.RegularExpressions.Regex.Match(loginHtml,
                @"<input[^>]*name=""__RequestVerificationToken""[^>]*value=""([^""]+)""").Groups[1].Value;
            var login = await client.PostAsync("/auth/login", new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    { "username", "admin" },
                    { "password", "admin" },
                    { "__RequestVerificationToken", token }
                }));
            login.EnsureSuccessStatusCode();

            var response = await client.PostAsync("/reidentify/run",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    { "jobId", jobId.ToString() },
                    { "save", "true" },
                    { "renameFiles", "true" }
                }));
            Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);

            var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var results = json.RootElement.GetProperty("renameResults").EnumerateArray().ToList();

            Assert.Equal(9, results.Count);
            Assert.All(results, r => Assert.Equal("renamed", r.GetProperty("status").GetString()));

            // Every new path exists; no old path remains; no staging leftovers.
            for (var i = 0; i < 9; i++)
            {
                var newPath = Path.Combine(seasonDir, $"S01E{6 + i:D2} - Old Episode {7 + i}.mkv");
                Assert.True(File.Exists(newPath), $"Expected {newPath} to exist");
                Assert.False(File.Exists(oldPaths[i]), $"Expected {oldPaths[i]} to have been renamed");
            }

            Assert.DoesNotContain(Directory.GetFiles(seasonDir), f => f.Contains(".reidentify-staging-"));

            // DB tracks now carry the new episode numbers.
            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ArmDbContext>();
                var tracks = await db.Tracks.Where(t => t.JobId == jobId).OrderBy(t => t.TrackNumber).ToListAsync();
                for (var i = 0; i < 9; i++)
                {
                    Assert.Equal(6 + i, tracks[i].EpisodeNumber);
                }
            }
        }
        finally
        {
            if (Directory.Exists(tempBase))
                Directory.Delete(tempBase, recursive: true);
        }
    }

    private sealed class FakeOrchestrator(EpisodeMap map) : IEpisodeIdentificationOrchestrator
    {
        public Task<EpisodeMap> IdentifyAsync(DiscContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(map);
    }
}