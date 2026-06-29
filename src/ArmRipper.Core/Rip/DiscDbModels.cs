using System.Text.Json;
using System.Text.Json.Serialization;

namespace ArmRipper.Core.Rip;

/// <summary>Response models for TheDiscDb GraphQL API.</summary>

public sealed class DiscDbGraphQlResponse
{
    [JsonPropertyName("data")]
    public DiscDbGraphQlData? Data { get; set; }

    /// <summary>
    /// GraphQL errors (if any). The API may return partial data plus errors,
    /// or null data plus errors. We log these as warnings.
    /// </summary>
    [JsonPropertyName("errors")]
    public List<DiscDbGraphQlError>? Errors { get; set; }
}

/// <summary>Represents a single GraphQL error returned by the API.</summary>
public sealed class DiscDbGraphQlError
{
    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("locations")]
    public List<DiscDbGraphQlErrorLocation>? Locations { get; set; }

    /// <summary>
    /// Path to the field that caused the error. May contain strings (field names)
    /// and numbers (array indices). We use JsonElement to handle the mixed types.
    /// </summary>
    [JsonPropertyName("path")]
    public List<JsonElement>? Path { get; set; }

    [JsonPropertyName("extensions")]
    public Dictionary<string, JsonElement>? Extensions { get; set; }
}

/// <summary>Location within a GraphQL document where an error occurred.</summary>
public sealed class DiscDbGraphQlErrorLocation
{
    [JsonPropertyName("line")]
    public int Line { get; set; }

    [JsonPropertyName("column")]
    public int Column { get; set; }
}

public sealed class DiscDbGraphQlData
{
    [JsonPropertyName("mediaItems")]
    public DiscDbMediaItems? MediaItems { get; set; }
}

public sealed class DiscDbMediaItems
{
    [JsonPropertyName("nodes")]
    public List<DiscDbMediaResult>? Nodes { get; set; }
}

public sealed class DiscDbMediaResult
{
    /// <summary>TheDiscDb internal media item ID (integer, NOT the content hash).</summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("year")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? Year { get; set; }

    [JsonPropertyName("slug")]
    public string? Slug { get; set; }

    [JsonPropertyName("imageUrl")]
    public string? ImageUrl { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }          // "Movie" | "Series"

    [JsonPropertyName("releases")]
    public List<DiscDbRelease>? Releases { get; set; }
}

public sealed class DiscDbRelease
{
    [JsonPropertyName("slug")]
    public string? Slug { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("discs")]
    public List<DiscDbDisc>? Discs { get; set; }
}

public sealed class DiscDbDisc
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("format")]
    public string? Format { get; set; }        // "DVD" | "BluRay" | "4K_UHD"

    /// <summary>TheDiscDb slug for this disc (e.g. "dvd").</summary>
    [JsonPropertyName("slug")]
    public string? Slug { get; set; }

    [JsonPropertyName("titles")]
    public List<DiscDbTitle>? Titles { get; set; }
}

public sealed class DiscDbTitle
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    /// <summary>
    /// Duration in seconds. Deserialized from either an integer or a "H:MM:SS"
    /// string via <see cref="DurationJsonConverter"/>.
    /// </summary>
    [JsonPropertyName("duration")]
    [JsonConverter(typeof(DurationJsonConverter))]
    public int? Duration { get; set; }

    [JsonPropertyName("displaySize")]
    public string? DisplaySize { get; set; }

    [JsonPropertyName("sourceFile")]
    public string? SourceFile { get; set; }

    [JsonPropertyName("size")]
    public long? Size { get; set; }

    [JsonPropertyName("segmentMap")]
    public string? SegmentMap { get; set; }

    [JsonPropertyName("item")]
    public DiscDbItem? Item { get; set; }
}

public sealed class DiscDbItem
{
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("season")]
    public int? Season { get; set; }

    [JsonPropertyName("episode")]
    public int? Episode { get; set; }

    /// <summary>
    /// Content type from TheDiscDb. Values like "MainMovie", "Extra",
    /// "DeletedScene" are automatically normalized to lowercase: "main",
    /// "extra", "deleted_scene" via <see cref="ItemTypeJsonConverter"/>.
    /// </summary>
    [JsonPropertyName("type")]
    [JsonConverter(typeof(ItemTypeJsonConverter))]
    public string? Type { get; set; }
}

/// <summary>Simplified flattened result for internal use.
/// Combines the media-level and track-level data into a single record.
/// Property names match the anonymous object serialized by DiscDbMappingService.</summary>
public sealed record DiscDbFlatTrack(
    [property: JsonPropertyName("index")] int TrackIndex,
    [property: JsonPropertyName("duration")] int? DurationSeconds,
    [property: JsonPropertyName("size")] long? FileSize,
    [property: JsonPropertyName("itemTitle")] string? ItemTitle,
    [property: JsonPropertyName("season")] int? Season,
    [property: JsonPropertyName("episode")] int? Episode,
    [property: JsonPropertyName("itemType")] string? ItemType
);

/// <summary>
/// Handles TheDiscDb's duration field which can be either an integer (seconds)
/// or a "H:MM:SS" string.
/// </summary>
public sealed class DurationJsonConverter : JsonConverter<int?>
{
    public override int? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.String => ParseDurationString(reader.GetString()),
            JsonTokenType.Number => reader.TryGetInt32(out var secs) ? secs : null,
            JsonTokenType.Null => null,
            _ => null
        };
    }

    public override void Write(Utf8JsonWriter writer, int? value, JsonSerializerOptions options)
    {
        if (value.HasValue)
            writer.WriteNumberValue(value.Value);
        else
            writer.WriteNullValue();
    }

    /// <summary>Parses "H:MM:SS" or "M:SS" or "SS" into total seconds.</summary>
    private static int? ParseDurationString(string? duration)
    {
        if (string.IsNullOrWhiteSpace(duration))
            return null;

        var parts = duration.Split(':');
        return parts.Length switch
        {
            3 => int.TryParse(parts[0], out var h) && int.TryParse(parts[1], out var m) && int.TryParse(parts[2], out var s)
                 ? h * 3600 + m * 60 + s : null,
            2 => int.TryParse(parts[0], out var m2) && int.TryParse(parts[1], out var s2)
                 ? m2 * 60 + s2 : null,
            1 => int.TryParse(parts[0], out var s1) ? s1 : null,
            _ => null
        };
    }
}

/// <summary>
/// Normalizes TheDiscDb item type values to lowercase snake_case for internal use.
/// "MainMovie" → "main", "Extra" → "extra", "DeletedScene" → "deleted_scene", etc.
/// </summary>
public sealed class ItemTypeJsonConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var raw = reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Null => null,
            _ => null
        };

        return raw is null ? null : NormalizeItemType(raw);
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value is null)
            writer.WriteNullValue();
        else
            writer.WriteStringValue(value);
    }

    /// <summary>Converts "MainMovie" → "main", "DeletedScene" → "deleted_scene", etc.</summary>
    public static string NormalizeItemType(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "unknown";

        // Insert underscore before uppercase letters preceded by lowercase
        var withUnderscores = System.Text.RegularExpressions.Regex.Replace(
            raw, "([a-z])([A-Z])", "$1_$2");

        return withUnderscores.ToLowerInvariant() switch
        {
            "main_movie" => "main",
            var other => other
        };
    }
}

/// <summary>
/// Handles TheDiscDb fields that can be either a string or an integer
/// (e.g. "year": 2003 vs "year": "2003"). Always outputs a string.
/// </summary>
public sealed class FlexibleStringConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => reader.GetInt32().ToString(),
            JsonTokenType.Null => null,
            _ => null
        };
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value is null)
            writer.WriteNullValue();
        else
            writer.WriteStringValue(value);
    }
}
