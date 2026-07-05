namespace ArmMedia.OvidProvider.Fingerprint;

/// <summary>
/// A single audio stream parsed from VTS IFO attributes.
/// </summary>
public sealed record AudioStream
{
    /// <summary>Audio codec name (e.g., "AC3", "DTS", "LPCM").</summary>
    public required string Codec { get; init; }

    /// <summary>ISO 639-2 language code (e.g., "en", "fr"), or empty.</summary>
    public required string Language { get; init; }

    /// <summary>Number of audio channels (e.g., 2, 6).</summary>
    public required int Channels { get; init; }
}

/// <summary>
/// A single subtitle stream parsed from VTS IFO attributes.
/// </summary>
public sealed record SubtitleStream
{
    /// <summary>ISO 639-2 language code (e.g., "en", "fr"), or empty.</summary>
    public required string Language { get; init; }
}

/// <summary>
/// A single Program Chain: playback duration and chapter count.
/// </summary>
public sealed record PgcInfo
{
    /// <summary>Total playback duration in seconds.</summary>
    public required int DurationSeconds { get; init; }

    /// <summary>Number of chapters (programs) in this PGC.</summary>
    public required int ChapterCount { get; init; }
}

/// <summary>
/// Parsed content of a VTS_XX_0.IFO file.
/// </summary>
public sealed record VtsInfo
{
    /// <summary>Program Chain list (PGCs = titles within this VTS).</summary>
    public IReadOnlyList<PgcInfo> PgcList { get; init; } = Array.Empty<PgcInfo>();

    /// <summary>Audio stream attributes.</summary>
    public IReadOnlyList<AudioStream> AudioStreams { get; init; } = Array.Empty<AudioStream>();

    /// <summary>Subtitle stream attributes.</summary>
    public IReadOnlyList<SubtitleStream> SubtitleStreams { get; init; } = Array.Empty<SubtitleStream>();
}

/// <summary>
/// Parsed content of VIDEO_TS.IFO (Video Manager).
/// </summary>
public sealed record VmgInfo
{
    /// <summary>Number of Video Title Sets.</summary>
    public required int VtsCount { get; init; }

    /// <summary>Number of titles (programs) in the TT_SRPT.</summary>
    public required int TitleCount { get; init; }
}
