using System.Text.Json.Serialization;

namespace ArmMedia.OvidProvider.Models;

/// <summary>
/// Response from GET /v1/disc/{fingerprint} on the OVID API.
/// </summary>
public sealed class OvidDiscLookupResponse
{
    /// <summary>Unique request identifier.</summary>
    [JsonPropertyName("request_id")]
    public string RequestId { get; set; } = string.Empty;

    /// <summary>The OVID fingerprint that was looked up.</summary>
    [JsonPropertyName("fingerprint")]
    public string Fingerprint { get; set; } = string.Empty;

    /// <summary>Disc format ("DVD", "Blu-ray", etc.).</summary>
    [JsonPropertyName("format")]
    public string Format { get; set; } = string.Empty;

    /// <summary>Disc status ("verified", "unverified", "disputed").</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    /// <summary>Confidence level ("high", "medium").</summary>
    [JsonPropertyName("confidence")]
    public string Confidence { get; set; } = string.Empty;

    /// <summary>Region code (e.g., "1", "2"), if known.</summary>
    [JsonPropertyName("region_code")]
    public string? RegionCode { get; set; }

    /// <summary>UPC/barcode, if known.</summary>
    [JsonPropertyName("upc")]
    public string? Upc { get; set; }

    /// <summary>Edition name (e.g., "Special Edition").</summary>
    [JsonPropertyName("edition_name")]
    public string? EditionName { get; set; }

    /// <summary>Disc number within a multi-disc set (1-based).</summary>
    [JsonPropertyName("disc_number")]
    public int DiscNumber { get; set; } = 1;

    /// <summary>Total discs in the set.</summary>
    [JsonPropertyName("total_discs")]
    public int TotalDiscs { get; set; } = 1;

    /// <summary>Release metadata (title, year, TMDB/IMDB IDs).</summary>
    [JsonPropertyName("release")]
    public OvidReleaseInfo? Release { get; set; }

    /// <summary>List of titles (playable tracks) on the disc.</summary>
    [JsonPropertyName("titles")]
    public List<OvidTitleInfo> Titles { get; set; } = [];
}

/// <summary>
/// Release metadata returned by the OVID API.
/// </summary>
public sealed class OvidReleaseInfo
{
    /// <summary>Movie or series title.</summary>
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    /// <summary>Release year.</summary>
    [JsonPropertyName("year")]
    public int? Year { get; set; }

    /// <summary>Content type ("movie", "tvshow", etc.).</summary>
    [JsonPropertyName("content_type")]
    public string ContentType { get; set; } = string.Empty;

    /// <summary>TMDB ID for cross-referencing.</summary>
    [JsonPropertyName("tmdb_id")]
    public int? TmdbId { get; set; }

    /// <summary>IMDB ID for cross-referencing.</summary>
    [JsonPropertyName("imdb_id")]
    public string? ImdbId { get; set; }
}

/// <summary>
/// A single title (playable track) on a disc, returned by the OVID API.
/// </summary>
public sealed class OvidTitleInfo
{
    /// <summary>Zero-based title index on the disc.</summary>
    [JsonPropertyName("title_index")]
    public int TitleIndex { get; set; }

    /// <summary>Whether this is the main feature.</summary>
    [JsonPropertyName("is_main_feature")]
    public bool IsMainFeature { get; set; }

    /// <summary>Title type classification (e.g., "main_feature", "trailer", "extra").</summary>
    [JsonPropertyName("title_type")]
    public string? TitleType { get; set; }

    /// <summary>Human-readable display name.</summary>
    [JsonPropertyName("display_name")]
    public string? DisplayName { get; set; }

    /// <summary>Duration in seconds.</summary>
    [JsonPropertyName("duration_secs")]
    public int? DurationSecs { get; set; }

    /// <summary>Number of chapters.</summary>
    [JsonPropertyName("chapter_count")]
    public int? ChapterCount { get; set; }

    /// <summary>Audio tracks on this title.</summary>
    [JsonPropertyName("audio_tracks")]
    public List<OvidTrackInfo> AudioTracks { get; set; } = [];

    /// <summary>Subtitle tracks on this title.</summary>
    [JsonPropertyName("subtitle_tracks")]
    public List<OvidTrackInfo> SubtitleTracks { get; set; } = [];
}

/// <summary>
/// A single audio or subtitle track on a disc title, returned by the OVID API.
/// </summary>
public sealed class OvidTrackInfo
{
    /// <summary>Zero-based track index.</summary>
    [JsonPropertyName("index")]
    public int Index { get; set; }

    /// <summary>ISO 639-2 language code.</summary>
    [JsonPropertyName("language")]
    public string? Language { get; set; }

    /// <summary>Codec name (e.g., "AC3", "DTS", "PGS").</summary>
    [JsonPropertyName("codec")]
    public string? Codec { get; set; }

    /// <summary>Number of audio channels.</summary>
    [JsonPropertyName("channels")]
    public int? Channels { get; set; }

    /// <summary>Whether this is the default stream.</summary>
    [JsonPropertyName("is_default")]
    public bool IsDefault { get; set; }
}
