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
[ArmMedia.Core.DiagnosticName(DiagnosticCategory)]
public sealed class FileBotProvider : IEpisodeIdentificationProvider
{
    private const string DiagnosticCategory = "FileBotProvider";
    /// <summary>Constant provider name used by both the sidecar and CLI identification paths.</summary>
    public const string ProviderNameConst = "FileBot";

    private readonly FileBotProviderOptions        _options;
    private readonly ILogger                       _logger;
    private readonly FileBotCliService?            _cliService;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas         = true
    };

    /// <summary>Initialises the provider with options, a logger, and an optional CLI service for runtime identification.</summary>
    public FileBotProvider(
        IOptions<FileBotProviderOptions>    options,
        ILoggerFactory                      loggerFactory,
        FileBotCliService?                  cliService = null)
    {
        _options = options.Value;
        _logger  = loggerFactory.CreateLogger(DiagnosticCategory);
        _cliService = cliService;
    }

    /// <inheritdoc/>
    public string ProviderName => ProviderNameConst;

    /// <inheritdoc/>
    public async Task<ProviderResult[]> IdentifyAsync(
        DiscContext       context,
        CancellationToken cancellationToken = default)
    {
        string mapPath = ResolveMapPath(context);

        // ── Priority 1: Pre-generated filebot-map.json sidecar ───────────────
        if (File.Exists(mapPath))
        {
            _logger.LogInformation("[FileBotProvider] Reading sidecar '{Path}'.", mapPath);

            FileBotMapFile? mapFile;
            try
            {
                await using var stream = File.OpenRead(mapPath);
                mapFile = await JsonSerializer.DeserializeAsync<FileBotMapFile>(stream, _jsonOptions, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[FileBotProvider] Failed to parse '{Path}'; falling back to CLI.", mapPath);
                mapFile = null;
            }

            if (mapFile?.Mappings is { Count: > 0 })
            {
                var results = mapFile.Mappings.Select(m => new ProviderResult
                {
                    TrackIndex   = m.TrackIndex,
                    Season       = m.Season,
                    Episodes     = m.Episodes ?? [0],
                    Title        = m.Title,
                    IsExtra      = m.IsExtra,
                    Confidence   = Confidence.High,
                    ProviderName = ProviderNameConst
                }).ToArray();

                _logger.LogInformation("[FileBotProvider] Loaded {Count} mappings from sidecar.", results.Length);
                return results;
            }
        }
        else
        {
            _logger.LogDebug("[FileBotProvider] Sidecar file not found at '{Path}'.", mapPath);
        }

        // ── Priority 2: Run FileBot CLI for live identification ──────────────
        if (_cliService is not null)
        {
            string? rawPath = ResolveRawPath(context);
            _logger.LogInformation("[FileBotProvider] Attempting CLI identification at '{Path}'.", rawPath ?? "(unknown)");
            return await _cliService.IdentifyAsync(context, rawPath, cancellationToken);
        }

        _logger.LogDebug("[FileBotProvider] No sidecar and no CLI service configured; returning empty results.");
        return [];
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private string ResolveMapPath(DiscContext context)
    {
        // Allow per-disc override by substituting {DiscId} in the configured path.
        return _options.MapFilePath
            .Replace("{DiscId}", context.DiscId, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Resolves the path to raw MakeMKV output files for FileBot CLI identification.</summary>
    private static string? ResolveRawPath(DiscContext context)
    {
        // Use the RawPath hint from the context's DiscDbHint / RawProperties,
        // or construct from known ARM-Sharp path conventions.
        return context.DiscDbHint; // The host sets this to the raw rip path
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
