using System.Net;
using ArmRipper.Core.Metadata;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArmRipper.Core.Tests;

public sealed class OmdbServiceTests
{
    private const string SearchResponse = """
        {
            "Search": [
                {
                    "Title": "The Matrix",
                    "Year": "1999",
                    "imdbID": "tt0133093",
                    "Type": "movie",
                    "Poster": "https://example.com/poster.jpg"
                }
            ],
            "Response": "True"
        }
        """;

    private const string ErrorResponse = """
        {
            "Response": "False",
            "Error": "Movie not found!"
        }
        """;

    [Fact]
    public async Task SearchAsync_WhenApiReturnsResults_ReturnsSearchResult()
    {
        var client = TestHelpers.CreateMockHttpClient(SearchResponse);
        var service = new OmdbService(NullLoggerFactory.Instance, client);

        var result = await service.SearchAsync("fake_key", "The Matrix");

        Assert.NotNull(result);
        Assert.Equal("True", result.Response);
        Assert.Single(result.Search!);
        Assert.Equal("The Matrix", result.Search![0].Title);
        Assert.Equal("tt0133093", result.Search[0].ImdbID);
    }

    [Fact]
    public async Task SearchAsync_WhenApiReturnsError_ReturnsNull()
    {
        var client = TestHelpers.CreateMockHttpClient(ErrorResponse);
        var service = new OmdbService(NullLoggerFactory.Instance, client);

        var result = await service.SearchAsync("fake_key", "NonExistentMovie");

        Assert.Null(result);
    }

    [Fact]
    public async Task SearchAsync_WhenApiReturns500_ReturnsNull()
    {
        var client = TestHelpers.CreateMockHttpClient("Server Error", HttpStatusCode.InternalServerError);
        var service = new OmdbService(NullLoggerFactory.Instance, client);

        var result = await service.SearchAsync("fake_key", "The Matrix");

        Assert.Null(result);
    }

    [Fact]
    public async Task LookupByImdbAsync_ValidId_ReturnsTitleInfo()
    {
        var response = """
            {
                "Title": "The Matrix",
                "Year": "1999",
                "imdbID": "tt0133093",
                "Type": "movie",
                "Poster": "https://example.com/poster.jpg",
                "Response": "True"
            }
            """;
        var client = TestHelpers.CreateMockHttpClient(response);
        var service = new OmdbService(NullLoggerFactory.Instance, client);

        var result = await service.LookupByImdbAsync("tt0133093", "fake_key");

        Assert.NotNull(result);
        Assert.Equal("The Matrix", result.Title);
        Assert.Equal("tt0133093", result.ImdbID);
    }

    [Fact]
    public async Task LookupByImdbAsync_InvalidId_ReturnsErrorResponse()
    {
        var client = TestHelpers.CreateMockHttpClient(ErrorResponse);
        var service = new OmdbService(NullLoggerFactory.Instance, client);

        var result = await service.LookupByImdbAsync("tt0000000", "fake_key");

        Assert.NotNull(result);
        Assert.Equal("False", result.Response);
        Assert.Equal("Movie not found!", result.Error);
    }

    [Fact]
    public async Task GetPosterAsync_WithImdbId_ReturnsPosterUrl()
    {
        var response = """
            {
                "Title": "The Matrix",
                "Poster": "https://example.com/poster.jpg",
                "imdbID": "tt0133093",
                "Response": "True"
            }
            """;
        var client = TestHelpers.CreateMockHttpClient(response);
        var service = new OmdbService(NullLoggerFactory.Instance, client);

        var (posterUrl, imdbId) = await service.GetPosterAsync("fake_key", imdbId: "tt0133093");

        Assert.Equal("https://example.com/poster.jpg", posterUrl);
        Assert.Equal("tt0133093", imdbId);
    }
}
