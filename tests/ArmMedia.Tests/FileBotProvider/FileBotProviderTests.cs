using ArmMedia.Core.Models;
using ArmMedia.FileBotProvider;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Text.Json;
using Xunit;

namespace ArmMedia.Tests.FileBotProvider;

/// <summary>
/// Tests for <see cref="ArmMedia.FileBotProvider.FileBotProvider"/> sidecar parsing.
/// </summary>
public sealed class FileBotProviderTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"filebottest_{Guid.NewGuid():N}");

    public FileBotProviderTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private ArmMedia.FileBotProvider.FileBotProvider MakeProvider(string mapPath)
    {
        var options = Options.Create(new FileBotProviderOptions { MapFilePath = mapPath });
        return new ArmMedia.FileBotProvider.FileBotProvider(options, NullLoggerFactory.Instance);
    }

    private DiscContext MakeContext(string discId = "TEST_DISC") => new()
    {
        DiscId      = discId,
        SeriesTitle = "Test Series",
        Season      = 1,
        Tracks      = []
    };

    // ─────────────────────────────────────────────────────────────────────────
    // TC-FB-01: Missing sidecar file → empty result, no exception
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task MissingSidecar_ReturnsEmpty_NoException()
    {
        var provider = MakeProvider(Path.Combine(_tempDir, "does-not-exist.json"));
        var results  = await provider.IdentifyAsync(MakeContext());
        Assert.Empty(results);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // TC-FB-02: Valid sidecar with multi-part entry is parsed correctly
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ValidSidecar_ParsesMultiPartEntry()
    {
        var sidecar = new
        {
            discId   = "TEST_DISC",
            mappings = new[]
            {
                new { trackIndex = 1, season = 1, episodes = new[] { 1 },    title = "Pilot",         isExtra = false },
                new { trackIndex = 2, season = 1, episodes = new[] { 2, 3 }, title = "Two Parter",    isExtra = false },
                new { trackIndex = 4, season = 0, episodes = new[] { 0 },    title = "Behind Scenes", isExtra = true  }
            }
        };

        string path = Path.Combine(_tempDir, "filebot-map.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(sidecar));

        var provider = MakeProvider(path);
        var results  = await provider.IdentifyAsync(MakeContext());

        Assert.Equal(3, results.Length);

        var multiPart = results.Single(r => r.TrackIndex == 2);
        Assert.Equal(new[] { 2, 3 }, multiPart.Episodes);

        var extra = results.Single(r => r.TrackIndex == 4);
        Assert.True(extra.IsExtra);
        Assert.Equal(0, extra.Season);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // TC-FB-03: Corrupt JSON → empty result, no exception
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CorruptJson_ReturnsEmpty_NoException()
    {
        string path = Path.Combine(_tempDir, "filebot-map.json");
        await File.WriteAllTextAsync(path, "{ invalid json {{{{");

        var provider = MakeProvider(path);
        var results  = await provider.IdentifyAsync(MakeContext());
        Assert.Empty(results);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // TC-FB-04: Empty mappings array → empty result
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EmptyMappingsArray_ReturnsEmpty()
    {
        string path = Path.Combine(_tempDir, "filebot-map.json");
        await File.WriteAllTextAsync(path, """{"discId":"TEST","mappings":[]}""");

        var provider = MakeProvider(path);
        var results  = await provider.IdentifyAsync(MakeContext());
        Assert.Empty(results);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // TC-FB-05: Provider name is "FileBot"
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ProviderName_IsFileBot()
    {
        var provider = MakeProvider("/any/path.json");
        Assert.Equal("FileBot", provider.ProviderName);
    }
}
