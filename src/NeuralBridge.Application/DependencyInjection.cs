using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using NeuralBridge.Application.Calls;
using NeuralBridge.Application.Documents;
using NeuralBridge.Application.Location;
using NeuralBridge.Application.Options;
using NeuralBridge.Application.Sessions;
using NeuralBridge.Application.Snippets;

namespace NeuralBridge.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddNeuralBridgeApplication(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<SessionOptions>()
            .Bind(configuration.GetSection(SessionOptions.SectionName))
            .PostConfigure(o => o.ApplyDefaults())
            .ValidateDataAnnotations()
            .Validate(o => o.AllowedLifetimesMinutes.Contains(o.DefaultLifetimeMinutes), "DefaultLifetimeMinutes must be one of AllowedLifetimesMinutes.")
            .Validate(o => o.AllowedLifetimesMinutes.All(m => m > 0 && m <= o.MaxTotalLifetimeHours * 60), "Allowed lifetimes must be positive and within MaxTotalLifetimeHours.")
            .Validate(o => o.PinMinLength <= o.PinMaxLength, "PinMinLength must not exceed PinMaxLength.")
            .ValidateOnStart();
        services.AddOptions<RateLimitOptions>()
            .Bind(configuration.GetSection(RateLimitOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<LocationOptions>()
            .Bind(configuration.GetSection(LocationOptions.SectionName))
            .Validate(o => Uri.TryCreate(o.ReverseGeocodingEndpoint, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps, "ReverseGeocodingEndpoint must be an absolute https URL.")
            .ValidateOnStart();

        services.AddOptions<CallOptions>()
            .Bind(configuration.GetSection(CallOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(o => o.IceServers.All(s => s.Urls.Length > 0 && s.Urls.All(u => u.StartsWith("stun:", StringComparison.Ordinal) || u.StartsWith("turn:", StringComparison.Ordinal) || u.StartsWith("turns:", StringComparison.Ordinal))), "Each ICE server needs stun:, turn: or turns: URLs.")
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);
        services.AddMemoryCache();

        // Application services are stateless; state lives behind the injected ports.
        services.AddSingleton<SessionCodeAllocator>();
        services.AddSingleton<ISessionAuthorizationService, SessionAuthorizationService>();
        services.AddSingleton<ISessionService, SessionService>();
        services.AddSingleton<ISessionOwnerService, SessionOwnerService>();
        services.AddSingleton<IDocumentMergeStrategy, LastWriteWinsMergeStrategy>();
        services.AddSingleton<ITextSynchronizationService, TextSynchronizationService>();
        services.AddSingleton<ISessionCleanupService, SessionCleanupService>();
        services.AddSingleton<ISnippetService, SnippetService>();
        services.AddSingleton<IAccountDataService, AccountDataService>();
        services.AddSingleton<ICallService, CallService>();
        services.AddScoped<ILocationDisplayService, LocationDisplayService>(); // scoped: depends on a typed HttpClient

        return services;
    }

    /// <summary>Convenience accessor used by the UI to render choices that match server validation.</summary>
    public static SessionOptions GetSessionOptions(this IServiceProvider provider) =>
        provider.GetRequiredService<IOptions<SessionOptions>>().Value;
}
