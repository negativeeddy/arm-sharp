using System.Text.Json;
using ArmMedia.Core.Abstractions;
using ArmMedia.TvdbProvider;
using ArmRipper.Core.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ArmRipper.Core;

/// <summary>
/// Resolves the TVDB API key by checking the DB-stored override first,
/// then falling back to the <c>Tvdb:ApiKey</c> appsettings.json configuration.
/// Uses a scoped DB query on each call so Web UI changes take effect immediately.
/// </summary>
public sealed class TvdbApiKeyResolver : ITvdbApiKeySource
{
    private readonly IServiceScopeFactory              _scopeFactory;
    private readonly IOptionsMonitor<TvdbProviderOptions> _tvdbOptions;

    /// <summary>Initialises the resolver with a scope factory and TVDB options.</summary>
    public TvdbApiKeyResolver(
        IServiceScopeFactory              scopeFactory,
        IOptionsMonitor<TvdbProviderOptions> tvdbOptions)
    {
        _scopeFactory  = scopeFactory;
        _tvdbOptions   = tvdbOptions;
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
                if (doc.RootElement.TryGetProperty("TvdbApiKey", out var el) &&
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
        if (!string.IsNullOrWhiteSpace(_tvdbOptions.CurrentValue.ApiKey))
            return _tvdbOptions.CurrentValue.ApiKey;

        return null;
    }
}
