using ArmRipper.Core.Metadata;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArmRipper.Core.Tests;

public sealed class TmdbServiceTests
{
    private const string MovieSearchResponse = """
        {
            "page": 1,
            "results": [
                {
                    "id": 603,
                    "title": "The Matrix",
                    "overview": "A computer hacker learns about the true nature of reality.",
                    "release_date": "1999-03-31",
                    "poster_path": "/matrix_poster.jpg",
                    "backdrop_path": "/matrix_bg.jpg",
                    "vote_average": 8.2,
                    "vote_count": 25000
                }
            ],
            "total_results": 1,
            "total_pages": 1
        }
        """;

    private const string TvSearchResponse = """
        {
            "page": 1,
            "results": [
                {
                    "id": 1399,
                    "name": "Game of Thrones",
                    "overview": "Nine noble families fight for control.",
                    "first_air_date": "2011-04-17",
                    "poster_path": "/got_poster.jpg",
                    "backdrop_path": "/got_bg.jpg",
                    "vote_average": 8.4,
                    "vote_count": 22000
                }
            ],
            "total_results": 1,
            "total_pages": 1
        }
        """;

    private const string EmptyResponse = """
        {
            "page": 1,
            "results": [],
            "total_results": 0,
            "total_pages": 0
        }
        """;

    [Fact]
    public async Task SearchMovieAsync_FindsMovie_ReturnsProcessedResult()
    {
        var client = TestHelpers.CreateMockHttpClient(MovieSearchResponse);
        var service = new TmdbService(NullLogger<TmdbService>.Instance, client);

        var result = await service.SearchMovieAsync("fake_key", "The Matrix");

        Assert.NotNull(result);
        Assert.Equal("The Matrix", result.Title);
        Assert.Equal("1999", result.Year);
        Assert.Equal("movie", result.Type);
        Assert.NotNull(result.PosterUrl);
        Assert.Contains("matrix_poster.jpg", result.PosterUrl);
    }

    [Fact]
    public async Task SearchMovieAsync_FallsBackToTv_WhenMovieNotFound()
    {
        var handler = new ConditionalHttpMessageHandler();
        handler.AddResponse("/search/movie", EmptyResponse);
        handler.AddResponse("/search/tv", TvSearchResponse);
        var client = new HttpClient(handler);
        var service = new TmdbService(NullLogger<TmdbService>.Instance, client);

        var result = await service.SearchMovieAsync("fake_key", "Game of Thrones");

        Assert.NotNull(result);
        Assert.Equal("Game of Thrones", result.Title);
        Assert.Equal("2011", result.Year);
        Assert.Equal("series", result.Type);
    }

    private sealed class ConditionalHttpMessageHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _responses = new(StringComparer.OrdinalIgnoreCase);

        public void AddResponse(string urlContains, string responseJson)
        {
            _responses[urlContains] = responseJson;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri?.ToString() ?? "";
            foreach (var (key, json) in _responses)
            {
                if (url.Contains(key, StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                    {
                        Content = new StringContent(json)
                    });
                }
            }
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }
    }

    [Fact]
    public async Task SearchMovieAsync_NoResults_ReturnsNull()
    {
        var client = TestHelpers.CreateMockHttpClient(EmptyResponse);
        var service = new TmdbService(NullLogger<TmdbService>.Instance, client);

        var result = await service.SearchMovieAsync("fake_key", "NonExistentMovie");

        Assert.Null(result);
    }

    [Fact]
    public async Task FindByImdbAsync_FindsMovie_ReturnsProcessedResult()
    {
        var response = """
            {
                "movie_results": [
                    {
                        "id": 603,
                        "title": "The Matrix",
                        "release_date": "1999-03-31",
                        "poster_path": "/matrix_poster.jpg",
                        "backdrop_path": "/matrix_bg.jpg",
                        "overview": "A computer hacker learns."
                    }
                ],
                "tv_results": []
            }
            """;
        var client = TestHelpers.CreateMockHttpClient(response);
        var service = new TmdbService(NullLogger<TmdbService>.Instance, client);

        var result = await service.FindByImdbAsync("tt0133093", "fake_key");

        Assert.NotNull(result);
        Assert.Equal("The Matrix", result.Title);
        Assert.Equal("tt0133093", result.ImdbId);
        Assert.Equal("movie", result.Type);
    }

    [Fact]
    public async Task FindByImdbAsync_ReturnsNull_WhenNoResults()
    {
        var response = """
            {
                "movie_results": [],
                "tv_results": []
            }
            """;
        var client = TestHelpers.CreateMockHttpClient(response);
        var service = new TmdbService(NullLogger<TmdbService>.Instance, client);

        var result = await service.FindByImdbAsync("tt0000000", "fake_key");

        Assert.Null(result);
    }

    [Fact]
    public async Task GetPosterAsync_WithValidQuery_ReturnsPoster()
    {
        var client = TestHelpers.CreateMockHttpClient(MovieSearchResponse);
        var service = new TmdbService(NullLogger<TmdbService>.Instance, client);

        var (posterUrl, imdbId) = await service.GetPosterAsync("fake_key", "The Matrix");

        Assert.NotNull(posterUrl);
        Assert.Contains("matrix_poster.jpg", posterUrl);
    }

    [Fact]
    public async Task GetPosterAsync_NoResults_ReturnsNull()
    {
        var client = TestHelpers.CreateMockHttpClient(EmptyResponse);
        var service = new TmdbService(NullLogger<TmdbService>.Instance, client);

        var (posterUrl, imdbId) = await service.GetPosterAsync("fake_key", "NonExistent");

        Assert.Null(posterUrl);
        Assert.Null(imdbId);
    }
}
