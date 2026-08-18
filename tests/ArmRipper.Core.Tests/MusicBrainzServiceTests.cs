using System.Net;
using ArmRipper.Core.Configuration;
using ArmRipper.Core.Infrastructure;
using ArmRipper.Core.Infrastructure.Data;
using ArmRipper.Core.Rip;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace ArmRipper.Core.Tests;

public sealed class MusicBrainzServiceTests : IDisposable
{
    private readonly ArmDbContext _db;
    private readonly Mock<ICliProcessRunner> _runnerMock;
    private readonly IOptions<ArmSettings> _options;

    public MusicBrainzServiceTests()
    {
        _db = TestHelpers.CreateDbContext();
        _runnerMock = new Mock<ICliProcessRunner>();
        _options = TestHelpers.CreateOptions();
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    private static string MusicBrainzXml(string discId = "abc123", string artist = "Test Artist", string title = "Test Album",
        string? offsetCount = "2")
    {
        return $""""
            <?xml version="1.0" encoding="UTF-8"?>
            <metadata xmlns="http://musicbrainz.org/ns/mmd-2.0#">
              <disc id="{discId}">
                <offset-list><offset>150</offset></offset-list>
                <offset-count>{offsetCount}</offset-count>
                <release-list count="1">
                  <release id="rel123">
                    <title>{title}</title>
                    <date>2024-06-15</date>
                    <artist-credit>
                      <name-credit>
                        <artist id="art123">
                          <name>{artist}</name>
                        </artist>
                      </name-credit>
                    </artist-credit>
                    <medium-list count="1">
                      <medium>
                        <format>CD</format>
                        <track-list count="2">
                          <track number="1">
                            <recording id="rec1">
                              <title>Track 1</title>
                              <length>300000</length>
                            </recording>
                          </track>
                          <track number="2">
                            <recording id="rec2">
                              <title>Track 2</title>
                              <length>240000</length>
                            </recording>
                          </track>
                        </track-list>
                      </medium>
                    </medium-list>
                    <cover-art-archive>
                      <artwork>true</artwork>
                    </cover-art-archive>
                  </release>
                </release-list>
              </disc>
            </metadata>
            """";
    }

    private static string CdStubXml(string artist = "Stub Artist", string title = "Stub Album", int trackCount = 2)
    {
        return $""""
            <?xml version="1.0" encoding="UTF-8"?>
            <metadata xmlns="http://musicbrainz.org/ns/mmd-2.0#">
              <cdstub id="stub456">
                <title>{title}</title>
                <artist>{artist}</artist>
                <track-count>{trackCount}</track-count>
                <track-list count="2">
                  <track>
                    <title>Stub Track 1</title>
                    <length>300000</length>
                  </track>
                  <track>
                    <title>Stub Track 2</title>
                    <length>240000</length>
                  </track>
                </track-list>
              </cdstub>
            </metadata>
            """";
    }

    private static string CoverArtJson(string imageUrl = "https://coverart.example.com/image.jpg")
    {
        return $$"""{ "images": [ { "image": "{{imageUrl}}" } ] }""";
    }

    private static HttpClient CreateHttpClient(string musicBrainzXml, string? coverArtJson = null)
    {
        return TestHelpers.CreateMockHttpClient(request =>
        {
            var url = request.RequestUri?.ToString() ?? "";
            if (url.Contains("coverartarchive.org"))
            {
                return coverArtJson is not null
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(coverArtJson) }
                    : new HttpResponseMessage(HttpStatusCode.NotFound);
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(musicBrainzXml) };
        });
    }

    [Fact]
    public async Task IdentifyAsync_WhenDiscIdFails_ReturnsEmpty()
    {
        _runnerMock
            .Setup(r => r.RunAsync("discid", "/dev/sr0", It.IsAny<string?>(), 15_000, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CliResult(1, "", "device not found", false));

        var httpClient = CreateHttpClient(MusicBrainzXml());
        var service = new MusicBrainzService(
            _runnerMock.Object, NullLoggerFactory.Instance, _db, _options, httpClient);

        var job = TestHelpers.CreateTestJob(j => j.HasNiceTitle = false);
        _db.Jobs.Add(job);
        await _db.SaveChangesAsync();

        var result = await service.IdentifyAsync(job);

        Assert.Equal("", result);
        Assert.Null(job.CrcId);
        Assert.False(job.HasNiceTitle);
    }

    [Fact]
    public async Task IdentifyAsync_WhenDiscIdSucceeds_ReturnsArtistTitle()
    {
        _runnerMock
            .Setup(r => r.RunAsync("discid", "/dev/sr0", It.IsAny<string?>(), 15_000, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CliResult(0, "abc123\nsome other line", "", false));

        var httpClient = CreateHttpClient(MusicBrainzXml());
        var service = new MusicBrainzService(
            _runnerMock.Object, NullLoggerFactory.Instance, _db, _options, httpClient);

        var job = TestHelpers.CreateTestJob();
        _db.Jobs.Add(job);
        await _db.SaveChangesAsync();

        var result = await service.IdentifyAsync(job);

        Assert.Equal("Test Artist Test Album", result);
        Assert.Equal("rel123", job.CrcId);
        Assert.True(job.HasNiceTitle);
        Assert.Equal("2024", job.Year);
        Assert.Equal("Test Artist Test Album", job.Title);
        Assert.Equal("Test Artist Test Album", job.TitleAuto);
    }

    [Fact]
    public async Task IdentifyAsync_ParsesOffsetCount()
    {
        _runnerMock
            .Setup(r => r.RunAsync("discid", "/dev/sr0", It.IsAny<string?>(), 15_000, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CliResult(0, "abc123", "", false));

        var httpClient = CreateHttpClient(MusicBrainzXml(offsetCount: "5"));
        var service = new MusicBrainzService(
            _runnerMock.Object, NullLoggerFactory.Instance, _db, _options, httpClient);

        var job = TestHelpers.CreateTestJob();
        _db.Jobs.Add(job);
        await _db.SaveChangesAsync();

        await service.IdentifyAsync(job);

        Assert.Equal(5, job.NoOfTitles);
    }

    [Fact]
    public async Task IdentifyAsync_WithInvalidOffsetCount_DoesNotCrash()
    {
        _runnerMock
            .Setup(r => r.RunAsync("discid", "/dev/sr0", It.IsAny<string?>(), 15_000, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CliResult(0, "abc123", "", false));

        var httpClient = CreateHttpClient(MusicBrainzXml(offsetCount: "not-a-number"));
        var service = new MusicBrainzService(
            _runnerMock.Object, NullLoggerFactory.Instance, _db, _options, httpClient);

        var job = TestHelpers.CreateTestJob();
        _db.Jobs.Add(job);
        await _db.SaveChangesAsync();

        await service.IdentifyAsync(job);

        Assert.Null(job.NoOfTitles);
    }

    [Fact]
    public async Task IdentifyAsync_SavesTracksToDatabase()
    {
        _runnerMock
            .Setup(r => r.RunAsync("discid", "/dev/sr0", It.IsAny<string?>(), 15_000, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CliResult(0, "abc123", "", false));

        var httpClient = CreateHttpClient(MusicBrainzXml());
        var service = new MusicBrainzService(
            _runnerMock.Object, NullLoggerFactory.Instance, _db, _options, httpClient);

        var job = TestHelpers.CreateTestJob();
        _db.Jobs.Add(job);
        await _db.SaveChangesAsync();

        await service.IdentifyAsync(job);

        var tracks = await _db.Tracks.Where(t => t.JobId == job.Id).ToListAsync();
        Assert.Equal(2, tracks.Count);
        Assert.Contains(tracks, t => t.FileName == "Track 1");
        Assert.Contains(tracks, t => t.FileName == "Track 2");
        Assert.All(tracks, t => Assert.Equal("MusicBrainz", t.Source));
    }

    [Fact]
    public async Task IdentifyAsync_WhenXmlIsMalformed_ReturnsEmpty()
    {
        _runnerMock
            .Setup(r => r.RunAsync("discid", "/dev/sr0", It.IsAny<string?>(), 15_000, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CliResult(0, "abc123", "", false));

        var httpClient = CreateHttpClient("not valid xml");
        var service = new MusicBrainzService(
            _runnerMock.Object, NullLoggerFactory.Instance, _db, _options, httpClient);

        var job = TestHelpers.CreateTestJob();
        _db.Jobs.Add(job);
        await _db.SaveChangesAsync();

        var result = await service.IdentifyAsync(job);

        Assert.Equal("", result);
    }

    [Fact]
    public async Task IdentifyAsync_WhenMusicBrainzFails_ReturnsEmpty()
    {
        _runnerMock
            .Setup(r => r.RunAsync("discid", "/dev/sr0", It.IsAny<string?>(), 15_000, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CliResult(0, "abc123", "", false));

        var httpClient = TestHelpers.CreateMockHttpClient("", HttpStatusCode.InternalServerError);
        var service = new MusicBrainzService(
            _runnerMock.Object, NullLoggerFactory.Instance, _db, _options, httpClient);

        var job = TestHelpers.CreateTestJob();
        _db.Jobs.Add(job);
        await _db.SaveChangesAsync();

        var result = await service.IdentifyAsync(job);

        Assert.Equal("", result);
    }

    [Fact]
    public async Task IdentifyAsync_WithCdStub_ReturnsArtistTitle()
    {
        _runnerMock
            .Setup(r => r.RunAsync("discid", "/dev/sr0", It.IsAny<string?>(), 15_000, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CliResult(0, "abc123", "", false));

        var httpClient = CreateHttpClient(CdStubXml());
        var service = new MusicBrainzService(
            _runnerMock.Object, NullLoggerFactory.Instance, _db, _options, httpClient);

        var job = TestHelpers.CreateTestJob();
        _db.Jobs.Add(job);
        await _db.SaveChangesAsync();

        var result = await service.IdentifyAsync(job);

        Assert.Equal("Stub Artist Stub Album", result);
        Assert.Equal("stub456", job.CrcId);
        Assert.True(job.HasNiceTitle);
        Assert.Equal("", job.Year);
    }

    [Fact]
    public async Task IdentifyAsync_WithCdStub_ParsesTrackCount()
    {
        _runnerMock
            .Setup(r => r.RunAsync("discid", "/dev/sr0", It.IsAny<string?>(), 15_000, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CliResult(0, "abc123", "", false));

        var httpClient = CreateHttpClient(CdStubXml(trackCount: 5));
        var service = new MusicBrainzService(
            _runnerMock.Object, NullLoggerFactory.Instance, _db, _options, httpClient);

        var job = TestHelpers.CreateTestJob();
        _db.Jobs.Add(job);
        await _db.SaveChangesAsync();

        await service.IdentifyAsync(job);

        Assert.Equal(5, job.NoOfTitles);
    }

    [Fact]
    public async Task IdentifyAsync_WithCdStubAndInvalidTrackCount_DoesNotCrash()
    {
        _runnerMock
            .Setup(r => r.RunAsync("discid", "/dev/sr0", It.IsAny<string?>(), 15_000, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CliResult(0, "abc123", "", false));

        var xml = CdStubXml().Replace(">2<", ">invalid<");
        var httpClient = CreateHttpClient(xml);
        var service = new MusicBrainzService(
            _runnerMock.Object, NullLoggerFactory.Instance, _db, _options, httpClient);

        var job = TestHelpers.CreateTestJob();
        _db.Jobs.Add(job);
        await _db.SaveChangesAsync();

        await service.IdentifyAsync(job);

        Assert.Null(job.NoOfTitles);
    }

    [Fact]
    public async Task IdentifyAsync_SetsPosterUrlWhenCoverArtFound()
    {
        _runnerMock
            .Setup(r => r.RunAsync("discid", "/dev/sr0", It.IsAny<string?>(), 15_000, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CliResult(0, "abc123", "", false));

        var httpClient = CreateHttpClient(MusicBrainzXml(), CoverArtJson("https://art.example.com/cover.jpg"));
        var service = new MusicBrainzService(
            _runnerMock.Object, NullLoggerFactory.Instance, _db, _options, httpClient);

        var job = TestHelpers.CreateTestJob();
        _db.Jobs.Add(job);
        await _db.SaveChangesAsync();

        await service.IdentifyAsync(job);

        Assert.Equal("https://art.example.com/cover.jpg", job.PosterUrl);
    }

    [Fact]
    public async Task IdentifyAsync_SkipsCoverArtWhenNotAvailable()
    {
        _runnerMock
            .Setup(r => r.RunAsync("discid", "/dev/sr0", It.IsAny<string?>(), 15_000, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CliResult(0, "abc123", "", false));

        var httpClient = CreateHttpClient(MusicBrainzXml(), null);
        var service = new MusicBrainzService(
            _runnerMock.Object, NullLoggerFactory.Instance, _db, _options, httpClient);

        var job = TestHelpers.CreateTestJob();
        _db.Jobs.Add(job);
        await _db.SaveChangesAsync();

        await service.IdentifyAsync(job);

        Assert.Null(job.PosterUrl);
    }

    [Fact]
    public async Task IdentifyAsync_WhenGetAudioTitleIsNone_ReturnsEmpty()
    {
        var options = TestHelpers.CreateOptions(o => o.GetAudioTitle = "none");

        _runnerMock
            .Setup(r => r.RunAsync("discid", "/dev/sr0", It.IsAny<string?>(), 15_000, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CliResult(0, "abc123", "", false));

        var httpClient = CreateHttpClient(MusicBrainzXml());
        var service = new MusicBrainzService(
            _runnerMock.Object, NullLoggerFactory.Instance, _db, options, httpClient);

        var job = TestHelpers.CreateTestJob(j => j.HasNiceTitle = false);
        _db.Jobs.Add(job);
        await _db.SaveChangesAsync();

        var result = await service.IdentifyAsync(job);

        Assert.Equal("", result);
        Assert.False(job.HasNiceTitle);
    }

    [Fact]
    public async Task IdentifyAsync_WhenNoReleases_ReturnsEmpty()
    {
        _runnerMock
            .Setup(r => r.RunAsync("discid", "/dev/sr0", It.IsAny<string?>(), 15_000, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CliResult(0, "abc123", "", false));

        var xml = MusicBrainzXml().Replace(@"<release-list count=""1"">", @"<release-list count=""0"">")
            .Replace("<release id=\"rel123\">", "")
            .Replace("</release>", "");
        // Remove the release content properly
        xml = System.Text.RegularExpressions.Regex.Replace(xml,
            @"<release id=""rel123"">.*?</release>", "",
            System.Text.RegularExpressions.RegexOptions.Singleline);

        var httpClient = CreateHttpClient(xml);
        var service = new MusicBrainzService(
            _runnerMock.Object, NullLoggerFactory.Instance, _db, _options, httpClient);

        var job = TestHelpers.CreateTestJob();
        _db.Jobs.Add(job);
        await _db.SaveChangesAsync();

        var result = await service.IdentifyAsync(job);

        Assert.Equal("", result);
    }

    [Fact]
    public async Task IdentifyAsync_WhenFormatIsNotCd_SkipsRelease()
    {
        _runnerMock
            .Setup(r => r.RunAsync("discid", "/dev/sr0", It.IsAny<string?>(), 15_000, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CliResult(0, "abc123", "", false));

        var xml = MusicBrainzXml().Replace("<format>CD</format>", "<format>DVD</format>");
        var httpClient = CreateHttpClient(xml);
        var service = new MusicBrainzService(
            _runnerMock.Object, NullLoggerFactory.Instance, _db, _options, httpClient);

        var job = TestHelpers.CreateTestJob();
        _db.Jobs.Add(job);
        await _db.SaveChangesAsync();

        var result = await service.IdentifyAsync(job);

        Assert.Equal("", result);
    }
}
