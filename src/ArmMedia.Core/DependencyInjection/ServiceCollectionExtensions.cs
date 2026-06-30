using ArmMedia.Core.Abstractions;
using ArmMedia.Core.Orchestration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ArmMedia.Core.DependencyInjection;

/// <summary>
/// Extension methods for registering the episode identification pipeline with
/// Microsoft.Extensions.DependencyInjection.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds core episode identification services and binds options from
    /// the <c>EpisodeIdentification</c> configuration section.
    /// </summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <param name="configuration">The application configuration root.</param>
    /// <returns>An <see cref="EpisodeIdentificationBuilder"/> for chaining provider registrations.</returns>
    public static EpisodeIdentificationBuilder AddEpisodeIdentification(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<EpisodeIdentificationOptions>(opt =>
            configuration.GetSection(EpisodeIdentificationOptions.SectionName).Bind(opt));

        services.AddSingleton<IEpisodeIdentificationOrchestrator, EpisodeIdentificationOrchestrator>();

        return new EpisodeIdentificationBuilder(services);
    }
}

/// <summary>
/// Fluent builder returned by <see cref="ServiceCollectionExtensions.AddEpisodeIdentification"/>
/// that allows chaining of provider registrations.
/// </summary>
public sealed class EpisodeIdentificationBuilder
{
    private readonly IServiceCollection _services;

    internal EpisodeIdentificationBuilder(IServiceCollection services) => _services = services;

    /// <summary>
    /// Registers a provider implementation with the DI container.
    /// Providers are evaluated in the order they are registered unless
    /// <see cref="EpisodeIdentificationOptions.ProviderOrder"/> overrides the sequence.
    /// </summary>
    /// <typeparam name="TProvider">A concrete type implementing <see cref="IEpisodeIdentificationProvider"/>.</typeparam>
    /// <returns>This builder instance for chaining.</returns>
    public EpisodeIdentificationBuilder AddProvider<TProvider>()
        where TProvider : class, IEpisodeIdentificationProvider
    {
        _services.AddSingleton<IEpisodeIdentificationProvider, TProvider>();
        return this;
    }
}
