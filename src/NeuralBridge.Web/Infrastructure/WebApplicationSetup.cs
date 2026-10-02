using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using NeuralBridge.Application.Options;
using NeuralBridge.Web.Api;
using NeuralBridge.Web.Auth;
using NeuralBridge.Web.Components;
using NeuralBridge.Web.Components.Session;
using NeuralBridge.Web.Components.Shared;
using NeuralBridge.Web.Hubs;
using NeuralBridge.Web.Security;

namespace NeuralBridge.Web.Infrastructure;

public static class WebApplicationSetup
{
    private const int MaxRealtimeMessageBytes = 512 * 1024; // > 50k chars of worst-case UTF-8 JSON

    public static IServiceCollection AddNeuralBridgeWeb(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var hosting = configuration.GetSection(HostingOptions.SectionName).Get<HostingOptions>() ?? new HostingOptions();
        services.AddOptions<HostingOptions>().Bind(configuration.GetSection(HostingOptions.SectionName));

        services.AddRazorComponents()
            .AddInteractiveServerComponents(options =>
            {
                options.DetailedErrors = environment.IsDevelopment();
                options.DisconnectedCircuitRetentionPeriod = TimeSpan.FromMinutes(3);
            })
            .AddHubOptions(options => options.MaximumReceiveMessageSize = MaxRealtimeMessageBytes);

        services.AddSignalR()
            .AddHubOptions<SessionHub>(options =>
            {
                options.MaximumReceiveMessageSize = MaxRealtimeMessageBytes;
                options.EnableDetailedErrors = environment.IsDevelopment();
            });

        services.AddNeuralBridgeAuthentication(configuration, environment);
        services.AddAntiforgery(options =>
        {
            options.Cookie.Name = "__nb_af";
            options.Cookie.SecurePolicy = AuthenticationSetup.CookieSecurityFor(configuration, environment);
        });

        var dataProtection = services.AddDataProtection().SetApplicationName("NeuralBridge");
        if (!string.IsNullOrWhiteSpace(hosting.DataProtectionKeysPath))
        {
            dataProtection.PersistKeysToFileSystem(new DirectoryInfo(hosting.DataProtectionKeysPath));
        }

        if (hosting.ForwardedHeadersEnabled)
        {
            services.Configure<ForwardedHeadersOptions>(options =>
            {
                options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

                // The deployment's reverse proxy is trusted (documented: only enable behind one).
                options.KnownIPNetworks.Clear();
                options.KnownProxies.Clear();
            });
        }

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http =>
            {
                // Fingerprinted static assets and the Blazor circuit transport are not counted:
                // one page view loads a dozen assets, and they carry no user data.
                if (IsUncountedPath(http.Request.Path))
                {
                    return RateLimitPartition.GetNoLimiter("static");
                }

                var limits = http.RequestServices.GetRequiredService<IOptions<RateLimitOptions>>().Value;
                return RateLimitPartition.GetFixedWindowLimiter(ClientContext.FromHttpContext(http), _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = limits.HttpRequestsPerMinute,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                });
            });
            options.AddPolicy(SessionApi.CreatePolicy, http => PerMinute(http, o => o.CreateSessionPerMinute));
            options.AddPolicy(SessionApi.JoinPolicy, http => PerMinute(http, o => o.JoinSessionPerMinute));
        });

        services.AddProblemDetails();
        services.AddHealthChecks();
        services.AddHttpContextAccessor();
        services.AddHsts(options =>
        {
            options.MaxAge = TimeSpan.FromDays(365);
            options.IncludeSubDomains = true;
        });

        // Per-circuit services
        services.AddScoped<ClientContext>();
        services.AddScoped<CircuitHandler, ClientContextCircuitHandler>();
        services.AddScoped<ToastService>();
        services.AddScoped<SessionCredentialStore>();
        services.AddScoped<WorkspacePresence>();

        // Hub plumbing
        services.AddSingleton<HubConnectionRegistry>();
        services.AddHostedService<HubNotificationRelay>();

        return services;
    }

    public static WebApplication UseNeuralBridgePipeline(this WebApplication app)
    {
        var hosting = app.Services.GetRequiredService<IOptions<HostingOptions>>().Value;

        if (hosting.ForwardedHeadersEnabled)
        {
            app.UseForwardedHeaders();
        }

        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler("/error", createScopeForErrors: true);
            app.UseHsts();
        }

        app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
        if (hosting.HttpsRedirection)
        {
            app.UseHttpsRedirection();
        }

        app.UseMiddleware<SecurityHeadersMiddleware>();
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseAntiforgery();

        app.MapStaticAssets();
        app.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode();
        app.MapHub<SessionHub>("/hubs/session", options =>
        {
            options.ApplicationMaxBufferSize = MaxRealtimeMessageBytes;
        });
        app.MapSessionApi();
        app.MapAccountEndpoints();
        app.MapHealthChecks("/healthz").DisableRateLimiting();

        return app;
    }

    private static readonly string[] UncountedPrefixes = ["/_framework/", "/_content/", "/_blazor", "/css/", "/js/", "/img/", "/icons/", "/fonts/"];

    internal static bool IsUncountedPath(PathString path) =>
        path == "/favicon.svg" ||
        path.Value is { } value && Array.Exists(UncountedPrefixes, p => value.StartsWith(p, StringComparison.OrdinalIgnoreCase)) ||
        path.Value?.EndsWith(".razor.js", StringComparison.OrdinalIgnoreCase) == true;

    private static RateLimitPartition<string> PerMinute(HttpContext http, Func<RateLimitOptions, int> permits)
    {
        var limits = http.RequestServices.GetRequiredService<IOptions<RateLimitOptions>>().Value;
        return RateLimitPartition.GetFixedWindowLimiter(ClientContext.FromHttpContext(http), _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permits(limits),
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        });
    }
}
