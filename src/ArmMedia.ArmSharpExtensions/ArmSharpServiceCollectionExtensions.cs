using ArmMedia.Core.Abstractions;
using ArmMedia.Core.DependencyInjection;
using ArmMedia.Core.Orchestration;
using ArmMedia.DvdCompareProvider;
using ArmMedia.FileBotProvider;
using ArmMedia.Linting;
using ArmMedia.Linting.Abstractions;
using ArmMedia.Linting.Rules;
using ArmMedia.Naming;
using ArmMedia.Naming.Abstractions;
using ArmMedia.OmdbProvider;
using ArmMedia.TmdbProvider;
using ArmMedia.TvdbProvider;
using ArmRipper.Core.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ArmMedia.ArmSharpExtensions;

/// <summary>
/// Service collection extensions for wiring the full ArmMedia episode identification
/// stack into an ARM-Sharp host application.
/// </summary>
public static class ArmSharpServiceCollectionExtensions
{
    /// <summary>
    /// Registers all ArmMedia services required by <see cref="ArmRipperServiceExtensions"/>:
    /// <list type="bullet">
    ///   <item>Episode identification orchestrator + DiscDb and FileBot providers</item>
    ///   <item>Default episode renamer</item>
    ///   <item>Default linting engine + built-in rules</item>
    /// </list>
    /// Call this once from your host's <c>Program.cs</c> or <c>Startup.ConfigureServices</c>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Application configuration (appsettings.json).</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddArmMediaTvPipeline(
        this IServiceCollection services,
        IConfiguration          configuration)
    {
        // ── Identification pipeline ───────────────────────────────────────────
        services
            .AddEpisodeIdentification(configuration)
            // Add providers in preferred order; ProviderOrder in config overrides eval order.
            .AddProvider<ArmMedia.DiscDbProvider.DiscDbProvider>()
            .AddProvider<ArmMedia.DvdCompareProvider.DvdCompareProvider>()
            .AddProvider<ArmMedia.FileBotProvider.FileBotProvider>()
            .AddProvider<ArmMedia.TmdbProvider.TmdbProvider>()
            .AddProvider<ArmMedia.TvdbProvider.TvdbProvider>()
            .AddProvider<ArmMedia.OmdbProvider.OmdbProvider>();

        // Bridge the existing IDiscDbMappingService to the lightweight
        // IDiscDbLookupService used by the provider layer.
        services.AddSingleton<IDiscDbLookupService, DiscDbLookupAdapter>();

        // ── DvdCompare provider options ─────────────────────────────────────
        services.Configure<DvdCompareProviderOptions>(
            configuration.GetSection(DvdCompareProviderOptions.SectionName));

        // ── FileBot CLI service ──────────────────────────────────────────────
        // Bind options so the provider can configure the FileBot database.
        services.Configure<FileBotProviderOptions>(
            configuration.GetSection(FileBotProviderOptions.SectionName));

        // Register the CLI runner delegate that bridges the host's
        // ICliProcessRunner to the FileBotCliService.
        services.AddSingleton<FileBotCliRunner>(sp =>
        {
            var runner = sp.GetRequiredService<ArmRipper.Core.Infrastructure.ICliProcessRunner>();
            return FileBotCliBridge.CreateRunner(runner);
        });
        services.AddSingleton<FileBotCliService>();

        // ── Naming ───────────────────────────────────────────────────────────
        services.Configure<NamingOptions>(configuration.GetSection(NamingOptions.SectionName));
        services.AddSingleton<IEpisodeRenamer, DefaultEpisodeRenamer>();

        // ── TMDB provider options ────────────────────────────────────────────
        // Bind the "Tmdb" appsettings section, then register a resolver that
        // merges the DB-stored ArmSettings.TmdbApiKey at runtime so Web UI
        // changes take effect without restarting the process.
        services.Configure<TmdbProviderOptions>(
            configuration.GetSection(TmdbProviderOptions.SectionName));
        services.AddSingleton<ITmdbApiKeySource, TmdbApiKeyResolver>();

        // ── TVDB provider options ────────────────────────────────────────────
        services.Configure<TvdbProviderOptions>(
            configuration.GetSection(TvdbProviderOptions.SectionName));
        services.AddSingleton<ITvdbApiKeySource, TvdbApiKeyResolver>();

        // ── OMDB provider options ────────────────────────────────────────────
        services.Configure<OmdbProviderOptions>(
            configuration.GetSection(OmdbProviderOptions.SectionName));
        services.AddSingleton<IOmdbApiKeySource, OmdbApiKeyResolver>();

        // ── Linting ──────────────────────────────────────────────────────────
        services.Configure<LintOptions>(configuration.GetSection(LintOptions.SectionName));
        services.AddSingleton<ILintRule, DuplicateEpisodeLintRule>();
        services.AddSingleton<ILintRule, EpisodeGapLintRule>();
        services.AddSingleton<ILintRule, LowConfidenceLintRule>();
        services.AddSingleton<ILintRule, RuntimeMismatchLintRule>();
        services.AddSingleton<ILintRule, MultiPartDurationMismatchLintRule>();
        services.AddSingleton<ILintRule, SeasonMismatchLintRule>();
        services.AddSingleton<ILintingEngine, DefaultLintingEngine>();

        return services;
    }
}
