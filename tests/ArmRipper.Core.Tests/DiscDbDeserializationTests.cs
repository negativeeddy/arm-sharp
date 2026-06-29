using System.Text.Json;
using System.Text.Json.Serialization;
using ArmRipper.Core.Rip;

namespace ArmRipper.Core.Tests.Rip;

public class DiscDbDeserializationTests
{
    /// <summary>
    /// Real response from TheDiscDb for "Freaky Friday (2003) DVD"
    /// captured at /workspaces/arm-sharp/.scratch/discdb-query-result.json.
    /// </summary>
    private const string FreakyFridayDvdJson = """
        {
            "data": {
                "mediaItems": {
                    "nodes": [
                        {
                            "id": 327,
                            "title": "Freaky Friday",
                            "year": 2003,
                            "slug": "freaky-friday-2003",
                            "imageUrl": "Movie/freaky-friday-2003/cover.jpg",
                            "type": "Movie",
                            "releases": [
                                {
                                    "slug": "2003-dvd",
                                    "title": "2003 DVD",
                                    "discs": [
                                        {
                                            "index": 1,
                                            "name": "DVD",
                                            "format": "DVD",
                                            "slug": "dvd",
                                            "titles": [
                                                {
                                                    "index": 0,
                                                    "duration": "1:36:47",
                                                    "displaySize": "3.3 GB",
                                                    "sourceFile": "01",
                                                    "size": 3594686464,
                                                    "segmentMap": "1,2,3,4,5,6,7,8,9,10,11,12,13",
                                                    "item": {
                                                        "title": "Freaky Friday Widescreen",
                                                        "season": null,
                                                        "episode": null,
                                                        "type": "MainMovie"
                                                    }
                                                },
                                                {
                                                    "index": 1,
                                                    "duration": "1:36:45",
                                                    "displaySize": "3.3 GB",
                                                    "sourceFile": "03",
                                                    "size": 3594438656,
                                                    "segmentMap": "1,2-3,4,5,6,7,8,9,10,11,12-13,14-15",
                                                    "item": {
                                                        "title": "Freaky Friday Fullscreen",
                                                        "season": null,
                                                        "episode": null,
                                                        "type": "MainMovie"
                                                    }
                                                },
                                                {
                                                    "index": 15,
                                                    "duration": "0:07:58",
                                                    "size": 315594752,
                                                    "item": {
                                                        "title": "Backstage Pass With Lindsay Lohan",
                                                        "season": null,
                                                        "episode": null,
                                                        "type": "Extra"
                                                    }
                                                },
                                                {
                                                    "index": 16,
                                                    "duration": "0:00:40",
                                                    "size": 20920320,
                                                    "item": {
                                                        "title": "Deleted Scene",
                                                        "season": null,
                                                        "episode": null,
                                                        "type": "DeletedScene"
                                                    }
                                                }
                                            ]
                                        }
                                    ]
                                }
                            ]
                        }
                    ]
                }
            }
        }
        """;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    [Fact]
    public void Deserialize_FreakyFridayDvd_AllFieldsMapped()
    {
        // Act
        var response = JsonSerializer.Deserialize<DiscDbGraphQlResponse>(FreakyFridayDvdJson, JsonOptions);

        // Assert — structure exists
        Assert.NotNull(response);
        Assert.NotNull(response!.Data);
        Assert.NotNull(response.Data.MediaItems);
        Assert.NotNull(response.Data.MediaItems!.Nodes);
        var node = Assert.Single(response.Data.MediaItems.Nodes);

        // Assert — media-level fields (including type coercions)
        Assert.Equal(327, node.Id);                   // integer id
        Assert.Equal("Freaky Friday", node.Title);
        Assert.Equal("2003", node.Year);              // integer → string
        Assert.Equal("freaky-friday-2003", node.Slug);
        Assert.Equal("Movie", node.Type);

        // Assert — releases/discs
        Assert.NotNull(node.Releases);
        var release = Assert.Single(node.Releases);
        Assert.Equal("2003-dvd", release.Slug);
        Assert.Equal("2003 DVD", release.Title);
        Assert.NotNull(release.Discs);
        var disc = Assert.Single(release.Discs);
        Assert.Equal(1, disc.Index);
        Assert.Equal("DVD", disc.Name);
        Assert.Equal("DVD", disc.Format);
        Assert.Equal("dvd", disc.Slug);

        // Assert — titles (we truncated to 4 representative entries)
        Assert.NotNull(disc.Titles);
        Assert.Equal(4, disc.Titles.Count);

        // Title 0: MainMovie, duration "1:36:47"
        var t0 = disc.Titles[0];
        Assert.Equal(0, t0.Index);
        Assert.Equal(1 * 3600 + 36 * 60 + 47, t0.Duration);  // "1:36:47" → 5807 seconds
        Assert.Equal(3594686464L, t0.Size);
        Assert.NotNull(t0.Item);
        Assert.Equal("Freaky Friday Widescreen", t0.Item!.Title);
        Assert.Equal("main", t0.Item!.Type);                  // "MainMovie" → "main"
        Assert.Null(t0.Item.Season);
        Assert.Null(t0.Item.Episode);

        // Title 15: Extra
        var t15 = disc.Titles[2];
        Assert.Equal(15, t15.Index);
        Assert.Equal(7 * 60 + 58, t15.Duration);              // "0:07:58" → 478 seconds
        Assert.NotNull(t15.Item);
        Assert.Equal("Backstage Pass With Lindsay Lohan", t15.Item!.Title);
        Assert.Equal("extra", t15.Item!.Type);                // "Extra" → "extra"

        // Title 16: DeletedScene
        var t16 = disc.Titles[3];
        Assert.Equal(16, t16.Index);
        Assert.Equal(40, t16.Duration);                       // "0:00:40" → 40 seconds
        Assert.NotNull(t16.Item);
        Assert.Equal("Deleted Scene", t16.Item!.Title);
        Assert.Equal("deleted_scene", t16.Item!.Type);        // "DeletedScene" → "deleted_scene"
    }

    [Fact]
    public void Deserialize_FreakyFridayDvd_RoundTripsThroughFlatten()
    {
        // Act
        var response = JsonSerializer.Deserialize<DiscDbGraphQlResponse>(FreakyFridayDvdJson, JsonOptions);
        var node = response!.Data!.MediaItems!.Nodes![0];

        // Flatten all titles (as DiscDbMappingService does)
        var flatTracks = node.Releases!
            .SelectMany(r => r.Discs ?? [])
            .SelectMany(d => d.Titles ?? [])
            .Select(t => new DiscDbFlatTrack(
                t.Index,
                t.Duration,
                t.Size,
                t.Item?.Title,
                t.Item?.Season,
                t.Item?.Episode,
                t.Item?.Type
            ))
            .ToList();

        Assert.Equal(4, flatTracks.Count);

        var mainMovie = flatTracks[0];
        Assert.Equal(0, mainMovie.TrackIndex);
        Assert.Equal(5807, mainMovie.DurationSeconds);
        Assert.Equal("main", mainMovie.ItemType);

        var extra = flatTracks[2];
        Assert.Equal(15, extra.TrackIndex);
        Assert.Equal("extra", extra.ItemType);

        var deleted = flatTracks[3];
        Assert.Equal(16, deleted.TrackIndex);
        Assert.Equal("deleted_scene", deleted.ItemType);
    }

    [Theory]
    [InlineData("0:00:00", 0)]
    [InlineData("0:00:01", 1)]
    [InlineData("0:01:00", 60)]
    [InlineData("1:00:00", 3600)]
    [InlineData("1:36:47", 5807)]
    [InlineData("12:34:56", 45296)]
    public void DurationJsonConverter_ParsesCorrectly(string input, int expectedSeconds)
    {
        var json = $"{{\"duration\": \"{input}\"}}";
        var result = JsonSerializer.Deserialize<DiscDbTitle>(json, JsonOptions);
        Assert.NotNull(result);
        Assert.Equal(expectedSeconds, result!.Duration);
    }

    [Theory]
    [InlineData("MainMovie", "main")]
    [InlineData("Extra", "extra")]
    [InlineData("DeletedScene", "deleted_scene")]
    [InlineData("Trailer", "trailer")]
    [InlineData("BehindTheScenes", "behind_the_scenes")]
    [InlineData("Featurette", "featurette")]
    public void ItemTypeJsonConverter_NormalizesCorrectly(string input, string expected)
    {
        Assert.Equal(expected, ItemTypeJsonConverter.NormalizeItemType(input));
    }

    [Theory]
    [InlineData("\"hello\"", "hello")]
    [InlineData("42", "42")]
    [InlineData("null", null)]
    public void FlexibleStringConverter_HandlesBothTypes(string jsonValue, string? expected)
    {
        var json = $"{{\"year\": {jsonValue}}}";
        var result = JsonSerializer.Deserialize<DiscDbMediaResult>(json, JsonOptions);
        Assert.NotNull(result);
        Assert.Equal(expected, result!.Year);
    }

    [Fact]
    public void Deserialize_GraphQlErrorResponse_ParsesErrors()
    {
        var json = """
            {
                "data": null,
                "errors": [
                    {
                        "message": "Not Found",
                        "locations": [ { "line": 2, "column": 3 } ],
                        "path": ["mediaItems"]
                    }
                ]
            }
            """;

        var response = JsonSerializer.Deserialize<DiscDbGraphQlResponse>(json, JsonOptions);

        Assert.NotNull(response);
        Assert.Null(response!.Data);
        Assert.NotNull(response.Errors);
        var error = Assert.Single(response.Errors);
        Assert.Equal("Not Found", error!.Message);
        Assert.NotNull(error.Locations);
        var location = Assert.Single(error.Locations);
        Assert.Equal(2, location!.Line);
        Assert.Equal(3, location.Column);
        Assert.NotNull(error.Path);
        Assert.Equal("mediaItems", Assert.Single(error.Path).GetString());
    }

    [Fact]
    public void Deserialize_GraphQlPartialWithErrors_StillReturnsData()
    {
        var json = """
            {
                "data": {
                    "mediaItems": { "nodes": [] }
                },
                "errors": [
                    {
                        "message": "Some field failed to resolve",
                        "path": ["mediaItems", "nodes", 0, "releases"]
                    }
                ]
            }
            """;

        var response = JsonSerializer.Deserialize<DiscDbGraphQlResponse>(json, JsonOptions);

        Assert.NotNull(response);
        Assert.NotNull(response!.Data);
        Assert.NotNull(response.Data!.MediaItems);
        Assert.NotNull(response.Data.MediaItems!.Nodes);
        Assert.Empty(response.Data.MediaItems.Nodes);
        Assert.NotNull(response.Errors);
        var error = Assert.Single(response.Errors);
        Assert.Equal("Some field failed to resolve", error!.Message);
        Assert.NotNull(error!.Path);
        Assert.Equal(4, error.Path.Count);
        // String elements
        Assert.Equal("mediaItems", error.Path[0].GetString());
        Assert.Equal("nodes", error.Path[1].GetString());
        Assert.Equal("releases", error.Path[3].GetString());
        // Numeric element
        Assert.Equal(0, error.Path[2].GetInt32());
    }
}
