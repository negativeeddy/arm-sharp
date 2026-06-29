using System.Text.Json;
using ArmMedia.Core.Abstractions;
using ArmMedia.TmdbProvider;
using ArmRipper.Core.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ArmMedia.ArmSharpExtensions;

/// <summary>
/// Resolves the TMDB API key by checking the DB-stored override first,
/// then falling back to the <c>Tmdb:ApiKey</c> appsettings.json configuration.
/// Uses a scoped DB query on each call so Web UI changes take effect immediately.
/// </summary>
public sealed class TmdbApiKeyResolver : ITmdbApiKeySource
{
    private readonly IServiceScopeFactory                   _scopeFactory;
    private readonly IOptionsMonitor<TmdbProviderOptions>     _tmdbOptions;

    /// <summary>Initialises the resolver with a scope factory and TMDB options.</summary>
    public TmdbApiKeyResolver(
        IServiceScopeFactory                scopeFactory,
        IOptionsMonitor<TmdbProviderOptions>  tmdbOptions)
    {
        _scopeFactory = scopeFactory;
        _tmdbOptions  = tmdbOptions;
    }

    /// <inheritdoc/>
    public string? GetApiKey()
    {
        // 1. Check the DB for a Web UI override
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ArmDbContext>();
            var saved = db.RipperSettings.FirstOrDefault();
            if (saved?.SettingsJson is not null)
            {
                using var doc = JsonDocument.Parse(saved.SettingsJson);
                if (doc.RootElement.TryGetProperty("TmdbApiKey", out var el) &&
                    el.ValueKind == JsonValueKind.String)
                {
                    var key = el.GetString();
                    if (!string.IsNullOrWhiteSpace(key))
                        return key;
                }
            }
        }
        catch
        {
            // Best-effort: if DB is unavailable, fall through to config
        }

        // 2. Fallback to appsettings.json / YAML config
        if (!string.IsNullOrWhiteSpace(_tmdbOptions.CurrentValue.ApiKey))
            return _tmdbOptions.CurrentValue.ApiKey;

        return null;
    }
}
