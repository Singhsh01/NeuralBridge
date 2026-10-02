using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NeuralBridge.Application.Abstractions;
using NeuralBridge.Application.Calls;
using NeuralBridge.Application.Options;
using NeuralBridge.Application.Realtime;
using NeuralBridge.Infrastructure.Hosting;
using NeuralBridge.Infrastructure.Location;
using NeuralBridge.Infrastructure.Persistence;
using NeuralBridge.Infrastructure.Persistence.EntityFramework;
using NeuralBridge.Infrastructure.Persistence.InMemory;
using NeuralBridge.Infrastructure.Realtime;
using NeuralBridge.Infrastructure.Security;

namespace NeuralBridge.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddNeuralBridgeInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var persistence = configuration.GetSection(PersistenceOptions.SectionName).Get<PersistenceOptions>() ?? new PersistenceOptions();
        services.AddOptions<PersistenceOptions>().Bind(configuration.GetSection(PersistenceOptions.SectionName));

        // Security primitives
        services.AddSingleton<ISessionCodeGenerator, SessionCodeGenerator>();
        services.AddSingleton<ISecretHasher, Pbkdf2SecretHasher>();
        services.AddSingleton<ITokenService, TokenService>();
        services.AddSingleton<IAppRateLimiter, AppRateLimiter>();

        // Real-time, single-instance state (swap for Redis-backed implementations to scale out)
        services.AddSingleton<ISessionEventBus, InMemorySessionEventBus>();
        services.AddSingleton<IPresenceTracker, InMemoryPresenceTracker>();
        services.AddSingleton<IDocumentStore, InMemoryDocumentStore>();
        services.AddSingleton<ISessionLockProvider, SessionLockProvider>();
        services.AddSingleton<ICallRegistry, InMemoryCallRegistry>();

        // Persistence
        if (persistence.Provider == PersistenceProvider.InMemory)
        {
            services.AddSingleton<ISessionStore, InMemorySessionStore>();
            services.AddSingleton<ISnippetRepository, InMemorySnippetRepository>();
            services.AddSingleton<IDatabaseInitializer, NoOpDatabaseInitializer>();
        }
        else
        {
            EntityFrameworkPersistence.Add(services, configuration, persistence);
        }

        // Location (reverse geocoding is opt-in per user action and can be disabled globally)
        var location = configuration.GetSection(LocationOptions.SectionName).Get<LocationOptions>() ?? new LocationOptions();
        if (location.ReverseGeocodingEnabled)
        {
            services.AddHttpClient<IReverseGeocoder, NominatimReverseGeocoder>((sp, http) =>
            {
                var o = sp.GetRequiredService<IOptions<LocationOptions>>().Value;
                http.BaseAddress = new Uri(o.ReverseGeocodingEndpoint);
                http.Timeout = TimeSpan.FromSeconds(o.TimeoutSeconds);
                http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", o.UserAgent);
            });
        }
        else
        {
            services.AddSingleton<IReverseGeocoder, DisabledReverseGeocoder>();
        }

        // Hosted services: database first, then the periodic sweeper.
        services.AddHostedService<DatabaseInitializationHostedService>();
        services.AddHostedService<SessionCleanupHostedService>();
        services.AddHostedService<CallJanitorHostedService>();

        return services;
    }
}
