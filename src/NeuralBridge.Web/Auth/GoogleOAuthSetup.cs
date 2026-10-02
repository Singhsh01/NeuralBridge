using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Identity;

namespace NeuralBridge.Web.Auth;

/// <summary>
/// Google sign-in with the framework's OAuth 2.0 handler (authorization code flow + PKCE) and
/// Google's published endpoints. It uses only the shared framework, so the exact same handler
/// runs in production and against the mock provider in automated tests.
/// </summary>
public static class GoogleOAuthSetup
{
    public const string AuthorizationEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    public const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    public const string UserInformationEndpoint = "https://openidconnect.googleapis.com/v1/userinfo";
    public const string PictureClaim = "urn:google:picture";
    public const string EmailVerifiedClaim = "urn:google:email_verified";

    /// <summary>Endpoint overrides are honored only outside production (mock provider for tests).</summary>
    public static bool AllowsTestEndpoints(IHostEnvironment environment) =>
        environment.IsDevelopment() || environment.IsEnvironment("Testing");

    /// <summary>The authorization endpoint actually in use (also needed for the CSP form-action list).</summary>
    public static string EffectiveAuthorizationEndpoint(GoogleAuthOptions options, IHostEnvironment environment) =>
        AllowsTestEndpoints(environment) && !string.IsNullOrWhiteSpace(options.TestEndpoints?.AuthorizationEndpoint)
            ? options.TestEndpoints!.AuthorizationEndpoint!
            : AuthorizationEndpoint;

    public static AuthenticationBuilder AddNeuralBridgeGoogle(this AuthenticationBuilder builder, GoogleAuthOptions google, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(google);
        var overrides = AllowsTestEndpoints(environment) ? google.TestEndpoints : null;

        return builder.AddOAuth(AuthProviderStatus.GoogleScheme, "Google", options =>
        {
            options.ClientId = google.ClientId!;
            options.ClientSecret = google.ClientSecret!;
            options.CallbackPath = google.CallbackPath;
            options.AuthorizationEndpoint = overrides?.AuthorizationEndpoint ?? AuthorizationEndpoint;
            options.TokenEndpoint = overrides?.TokenEndpoint ?? TokenEndpoint;
            options.UserInformationEndpoint = overrides?.UserInformationEndpoint ?? UserInformationEndpoint;
            options.SignInScheme = IdentityConstants.ExternalScheme;
            options.UsePkce = true;
            options.SaveTokens = false; // NeuralBridge never calls Google APIs on the user's behalf
            options.Scope.Clear();
            options.Scope.Add("openid");
            options.Scope.Add("email");
            options.Scope.Add("profile");

            // The correlation cookie must survive the top-level GET redirect back from Google, also on plain-HTTP dev/LAN setups.
            options.CorrelationCookie.SameSite = SameSiteMode.Lax;
            options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;

            options.ClaimActions.MapJsonKey(ClaimTypes.NameIdentifier, "sub");
            options.ClaimActions.MapJsonKey(ClaimTypes.Name, "name");
            options.ClaimActions.MapJsonKey(ClaimTypes.GivenName, "given_name");
            options.ClaimActions.MapJsonKey(ClaimTypes.Surname, "family_name");
            options.ClaimActions.MapJsonKey(ClaimTypes.Email, "email");
            options.ClaimActions.MapJsonKey(PictureClaim, "picture");
            options.ClaimActions.MapCustomJson(EmailVerifiedClaim, json =>
                json.TryGetProperty("email_verified", out var v) && (v.ValueKind == JsonValueKind.True || (v.ValueKind == JsonValueKind.String && v.GetString() == "true"))
                    ? "true"
                    : "false");

            options.Events = new OAuthEvents
            {
                OnRedirectToAuthorizationEndpoint = context =>
                {
                    // Let people with several Google accounts pick one instead of silently reusing the last.
                    context.Response.Redirect(context.RedirectUri + "&prompt=select_account");
                    return Task.CompletedTask;
                },
                OnCreatingTicket = async context =>
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, context.Options.UserInformationEndpoint);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", context.AccessToken);
                    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                    using var response = await context.Backchannel.SendAsync(request, context.HttpContext.RequestAborted);
                    response.EnsureSuccessStatusCode();
                    using var profile = JsonDocument.Parse(await response.Content.ReadAsStringAsync(context.HttpContext.RequestAborted));
                    if (!profile.RootElement.TryGetProperty("sub", out _))
                    {
                        context.Fail("The identity provider did not return a subject identifier.");
                        return;
                    }

                    context.RunClaimActions(profile.RootElement);
                },
                OnAccessDenied = context =>
                {
                    context.Response.Redirect(LoginErrorUrl("google_cancelled", context.ReturnUrl));
                    context.HandleResponse();
                    return Task.CompletedTask;
                },
                OnRemoteFailure = context =>
                {
                    var logger = context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("NeuralBridge.Auth");
                    logger.LogWarning("Google sign-in failed: {Reason}", context.Failure?.GetType().Name ?? "unknown");
                    context.Response.Redirect(LoginErrorUrl("google_failed", null));
                    context.HandleResponse();
                    return Task.CompletedTask;
                },
            };
        });
    }

    private static string LoginErrorUrl(string error, string? returnUrl) =>
        $"/account/login?error={error}" + (returnUrl is null ? string.Empty : $"&returnUrl={Uri.EscapeDataString(AccountEndpoints.SafeReturnUrl(ExtractReturn(returnUrl)))}");

    // Our challenge RedirectUri is "/account/external-callback?returnUrl=...": recover the user's original target.
    private static string? ExtractReturn(string callbackUrl)
    {
        var q = callbackUrl.IndexOf("returnUrl=", StringComparison.Ordinal);
        return q < 0 ? null : Uri.UnescapeDataString(callbackUrl[(q + "returnUrl=".Length)..].Split('&')[0]);
    }
}
