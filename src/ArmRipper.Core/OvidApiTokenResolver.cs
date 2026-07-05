using System.Text.Json;
using ArmMedia.Core.Abstractions;
using ArmMedia.OvidProvider;
using ArmRipper.Core.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ArmRipper.Core;

/// <summary>
/// Resolves the OVID API token by checking the DB-stored override first,
/// then falling back to the <c>OvidProvider:ApiToken</c> appsettings.json
/// or YAML configuration.
/// Uses a scoped DB query on each call so Web UI changes take effect immediately.
/// </summary>
public sealed class OvidApiTokenResolver : IOvidApiTokenSource
{
    private readonly IServiceScopeFactory                    _scopeFactory;
    private readonly IOptionsMonitor<OvidProviderOptions>      _ovidOptions;

    /// <summary>Initialises the resolver with a scope factory and OVID options.</summary>
    public OvidApiTokenResolver(
        IServiceScopeFactory               scopeFactory,
        IOptionsMonitor<OvidProviderOptions> ovidOptions)
    {
        _scopeFactory = scopeFactory;
        _ovidOptions  = ovidOptions;
    }

    /// <inheritdoc/>
    public string? GetToken()
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
                if (doc.RootElement.TryGetProperty("OvidApiToken", out var el) &&
                    el.ValueKind == JsonValueKind.String)
                {
                    var token = el.GetString();
                    if (!string.IsNullOrWhiteSpace(token))
                        return token;
                }
            }
        }
        catch
        {
            // Best-effort: if DB is unavailable, fall through to config
        }

        // 2. Fallback to appsettings.json / YAML config
        if (!string.IsNullOrWhiteSpace(_ovidOptions.CurrentValue.ApiToken))
            return _ovidOptions.CurrentValue.ApiToken;

        return null;
    }
}
