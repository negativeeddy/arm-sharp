using ArmMedia.Core.Abstractions;
using ArmRipper.Core.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ArmRipper.Core;

/// <summary>
/// Resolves the TVDB API key from the effective (DB-first) settings via
/// <see cref="ISettingsService"/>. All DB-blob parsing and file fallbacks live
/// in one place (SettingsHelper) — no duplicated logic here.
/// </summary>
public sealed class TvdbApiKeyResolver : ITvdbApiKeySource
{
    private readonly IServiceScopeFactory _scopeFactory;

    /// <summary>Initialises the resolver with a scope factory.</summary>
    public TvdbApiKeyResolver(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    /// <inheritdoc/>
    public string? GetApiKey()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
            return settings.GetEffectiveAsync().GetAwaiter().GetResult().TvdbApiKey;
        }
        catch
        {
            return null;
        }
    }
}
