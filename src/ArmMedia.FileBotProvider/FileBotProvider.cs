using ArmMedia.Core.Abstractions;
using ArmMedia.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace ArmMedia.FileBotProvider;

/// <summary>
/// An <see cref="IEpisodeIdentificationProvider"/> that reads an optional
/// <c>filebot-map.json</c> sidecar file and returns episode assignments from it.
/// If the sidecar file is absent, this provider returns an empty result set
/// and logs a debug message — it never throws.
/// </summary>
public sealed class FileBotProvider : IEpisodeIdentificationProvider
{
    private readonly FileBotProviderOptions        _options;
    private readonly ILogger<FileBotProvider>      _logger;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas         = true
    };

    /// <summary>Initialises the provider with options and a logger.</summary>
    public FileBotProvider(
        IOptions<FileBotProviderOptions>    options,
        ILogger<FileBotProvider>            logger)
    {
        _options = options.Value;
        _logger  = logger;
    }

    /// <inheritdoc/>
    public string ProviderName => "FileBot";

    /// <inheritdoc/>
    public async Task<ProviderResult[]> IdentifyAsync(
        DiscContext       context,
        CancellationToken cancellationToken = default)
    {
        string mapPath = ResolveMapPath(context);

        if (!File.Exists(mapPath))
        {
            _logger.LogDebug("[FileBotProvider] Sidecar file not found at '{Path}'; returning empty results.", mapPath);
            return [];
        }

        _logger.LogInformation("[FileBotProvider] Reading sidecar '{Path}'.", mapPath);

        FileBotMapFile? mapFile;
        try
        {
            await using var stream = File.OpenRead(mapPath);
            mapFile = await JsonSerializer.DeserializeAsync<FileBotMapFile>(stream, _jsonOptions, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[FileBotProvider] Failed to parse '{Path}'; returning empty results.", mapPath);
            return [];
        }

        if (mapFile?.Mappings is null || mapFile.Mappings.Count == 0)
        {
            _logger.LogDebug("[FileBotProvider] Sidecar has no mappings; returning empty results.");
            return [];
        }

        var results = mapFile.Mappings.Select(m => new ProviderResult
        {
            TrackIndex   = m.TrackIndex,
            Season       = m.Season,
            Episodes     = m.Episodes ?? [0],
            Title        = m.Title,
            IsExtra      = m.IsExtra,
            Confidence   = Confidence.High,
            ProviderName = ProviderName
        }).ToArray();

        _logger.LogInformation("[FileBotProvider] Loaded {Count} mappings from sidecar.", results.Length);
        return results;
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private string ResolveMapPath(DiscContext context)
    {
        // Allow per-disc override by substituting {DiscId} in the configured path.
        return _options.MapFilePath
            .Replace("{DiscId}", context.DiscId, StringComparison.OrdinalIgnoreCase);
    }
}

// ── Internal DTO ─────────────────────────────────────────────────────────────

internal sealed class FileBotMapFile
{
    public string?                    DiscId    { get; set; }
    public List<FileBotMappingEntry>? Mappings  { get; set; }
}

internal sealed class FileBotMappingEntry
{
    public int     TrackIndex { get; set; }
    public int     Season     { get; set; }
    public int[]?  Episodes   { get; set; }
    public string? Title      { get; set; }
    public bool    IsExtra    { get; set; }
}
