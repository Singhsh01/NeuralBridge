using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Options;

namespace NeuralBridge.Web.Auth;

/// <summary>
/// Logs, once the server is listening, how sign-in is configured: never the client ID or
/// secret, only whether they are present and the exact redirect URI(s) to register with Google.
/// </summary>
internal sealed class AuthStartupReport : IHostedService
{
    private readonly IHostApplicationLifetime _lifetime;
    private readonly IServer _server;
    private readonly IOptions<GoogleAuthOptions> _google;
    private readonly AuthProviderStatus _status;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<AuthStartupReport> _logger;

    public AuthStartupReport(
        IHostApplicationLifetime lifetime,
        IServer server,
        IOptions<GoogleAuthOptions> google,
        AuthProviderStatus status,
        IHostEnvironment environment,
        ILogger<AuthStartupReport> logger)
    {
        _lifetime = lifetime;
        _server = server;
        _google = google;
        _status = status;
        _environment = environment;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _lifetime.ApplicationStarted.Register(Report);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    internal static IReadOnlyList<string> RedirectUris(IEnumerable<string> listenAddresses, string callbackPath) =>
        listenAddresses
            .Select(a => a.Replace("://+", "://localhost", StringComparison.Ordinal)
                          .Replace("://*", "://localhost", StringComparison.Ordinal)
                          .Replace("://0.0.0.0", "://localhost", StringComparison.Ordinal)
                          .Replace("://[::]", "://localhost", StringComparison.Ordinal)
                          .TrimEnd('/') + callbackPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private void Report()
    {
        var google = _google.Value;
        var addresses = _server.Features.Get<IServerAddressesFeature>()?.Addresses ?? [];
        var uris = RedirectUris(addresses, google.CallbackPath);

        if (google.IsConfigured)
        {
            _logger.LogInformation("Google sign-in is enabled. Authorized redirect URI(s) to register for this host: {RedirectUris}", string.Join(", ", uris));
            if (!google.ClientId!.EndsWith(".apps.googleusercontent.com", StringComparison.Ordinal))
            {
                _logger.LogWarning("Authentication:Google:ClientId does not look like a Google OAuth client ID (expected *.apps.googleusercontent.com)");
            }

            if (google.TestEndpoints is not null && GoogleOAuthSetup.AllowsTestEndpoints(_environment))
            {
                _logger.LogWarning("Google sign-in is using test endpoints (Authentication:Google:TestEndpoints): a mock provider, not Google");
            }
        }
        else if (google.ExpectedInProduction && !_environment.IsDevelopment())
        {
            _logger.LogWarning("Google sign-in is expected (Authentication:Google:ExpectedInProduction=true) but Authentication:Google:ClientId and/or ClientSecret are not set. The Google button is shown as unavailable; guests and local accounts are unaffected.");
        }
        else
        {
            _logger.LogInformation("Google sign-in is not configured (optional). To enable it, register {RedirectUris} and set Authentication:Google:ClientId/ClientSecret (see README).", string.Join(", ", uris));
        }

        _logger.LogInformation(
            "Sign-in methods: Google {Google}, email/password {Local}, password reset by email {Reset}",
            _status.GoogleAvailable,
            _status.LocalAccountsAvailable,
            _status.PasswordResetAvailable);
    }
}
