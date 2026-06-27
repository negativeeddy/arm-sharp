namespace ArmRipper.Core.Configuration;

/// <summary>
/// Central constants for media directory paths.
/// All code should reference these constants instead of duplicating path strings
/// or directory names. This ensures a single source of truth for the default paths
/// used throughout the application.
///
/// Source-type identifiers have been moved to the <see cref="FileSource"/> enum.
/// </summary>
public static class ArmPaths
{
    // ── Default filesystem paths (matching ArmSettings defaults) ──
    /// <summary>Default root media path.</summary>
    public const string DefaultMediaRoot = "/home/arm/media";
    /// <summary>Default raw/unprocessed rip output directory.</summary>
    public const string DefaultRawPath = "/home/arm/media/raw";
    /// <summary>Default transcode working directory.</summary>
    public const string DefaultTranscodePath = "/home/arm/media/transcode";
    /// <summary>Default final completed media directory.</summary>
    public const string DefaultCompletedPath = "/home/arm/media/completed";
    /// <summary>Default log directory.</summary>
    public const string DefaultLogPath = "/home/arm/logs";
    /// <summary>Default SQLite database path.</summary>
    public const string DefaultDbFile = "/etc/arm/config/arm-sharp.db";
    /// <summary>Default ARM install directory.</summary>
    public const string DefaultInstallPath = "/opt/arm";

    // ── Subdirectory / segment names ──
    /// <summary>Subdirectory name for data-disc rips.</summary>
    public const string DataDir = "data";
    /// <summary>Subdirectory name for extras (e.g. bonus features).</summary>
    public const string ExtrasDir = "extras";
    /// <summary>Subdirectory name for movies.</summary>
    public const string MoviesDir = "movies";

    /// <summary>
    /// Resolves which <see cref="FileSource"/> a file path belongs to by checking
    /// which configured base path the file path starts with.
    /// </summary>
    public static FileSource ResolveSourceType(string filePath, ArmSettings? settings)
    {
        var raw = GetRawPath(settings).TrimEnd('/');
        var transcode = GetTranscodePath(settings).TrimEnd('/');

        if (filePath.StartsWith(raw, StringComparison.OrdinalIgnoreCase))
            return FileSource.Raw;
        if (filePath.StartsWith(transcode, StringComparison.OrdinalIgnoreCase))
            return FileSource.Transcode;
        return FileSource.Completed;
    }

    /// <summary>Gets the configured base path for the given <see cref="FileSource"/>.</summary>
    public static string GetPathForSource(FileSource source, ArmSettings? settings) => source switch
    {
        FileSource.Raw => GetRawPath(settings),
        FileSource.Transcode => GetTranscodePath(settings),
        FileSource.Completed => GetCompletedPath(settings),
        _ => GetCompletedPath(settings)
    };

    /// <summary>Gets the effective raw path from settings, falling back to <see cref="DefaultRawPath"/>.</summary>
    public static string GetRawPath(ArmSettings? settings)
        => settings?.RawPath ?? DefaultRawPath;

    /// <summary>Gets the effective transcode path from settings, falling back to <see cref="DefaultTranscodePath"/>.</summary>
    public static string GetTranscodePath(ArmSettings? settings)
        => settings?.TranscodePath ?? DefaultTranscodePath;

    /// <summary>Gets the effective completed path from settings, falling back to <see cref="DefaultCompletedPath"/>.</summary>
    public static string GetCompletedPath(ArmSettings? settings)
        => settings?.CompletedPath ?? DefaultCompletedPath;

    /// <summary>Gets the effective log path from settings, falling back to <see cref="DefaultLogPath"/>.</summary>
    public static string GetLogPath(ArmSettings? settings)
        => settings?.LogPath ?? DefaultLogPath;
}
