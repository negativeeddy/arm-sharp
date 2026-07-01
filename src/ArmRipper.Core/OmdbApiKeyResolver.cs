using System.Text.Json;
using ArmMedia.Core.Abstractions;
using ArmMedia.OmdbProvider;
using ArmRipper.Core.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ArmRipper.Core;

/// <summary>
/// Resolves the OMDB API key by checking the DB-stored override first,
/// then falling back to the <c>Omdb:ApiKey</c> appsettings.json configuration.
/// Uses a scoped DB query so Web UI changes take effect immediately.
/// </summary>
public sealed class OmdbApiKeyResolver : IOmdbApiKeySource
{
    private readonly IServiceScopeFactory              _scopeFactory;
    private readonly IOptionsMonitor<OmdbProviderOptions> _omdbOptions;

    /// <summary>Initialises the resolver with a scope factory and OMDB options.</summary>
    public OmdbApiKeyResolver(
        IServiceScopeFactory              scopeFactory,
        IOptionsMonitor<OmdbProviderOptions> omdbOptions)
    {
        _scopeFactory = scopeFactory;
        _omdbOptions  = omdbOptions;
    }

    /// <inheritdoc/>
    public string? GetApiKey()
    {
        // 1. Check the DB for a Web UI override
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ArmDbContext>();
            var saved = db.RipperSettings.OrderBy(s => s.Id).FirstOrDefault();
            if (saved?.SettingsJson is not null)
            {
                using var doc = JsonDocument.Parse(saved.SettingsJson);
                if (doc.RootElement.TryGetProperty("OmdbApiKey", out var el) &&
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
        if (!string.IsNullOrWhiteSpace(_omdbOptions.CurrentValue.ApiKey))
            return _omdbOptions.CurrentValue.ApiKey;

        return null;
    }
}
