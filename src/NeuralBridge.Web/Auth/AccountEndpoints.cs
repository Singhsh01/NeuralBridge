using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using NeuralBridge.Infrastructure.Identity;

namespace NeuralBridge.Web.Auth;

/// <summary>HTTP endpoints for the parts of sign-in that must be real redirects: the Google round trip and sign-out.</summary>
public static class AccountEndpoints
{
    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        // Safe provider availability for any client. Never includes credentials or endpoints.
        app.MapGet("/api/auth/providers", (AuthProviderStatus status) => TypedResults.Ok(new
        {
            google = status.GoogleAvailable,
            localAccounts = status.LocalAccountsAvailable,
            passwordReset = status.PasswordResetAvailable,
        }));

        var group = app.MapGroup("/account");

        // POST + antiforgery (form from the Google button) prevents login CSRF.
        group.MapPost("/login/google", (
            [FromForm] string? returnUrl,
            AuthProviderStatus status,
            SignInManager<ApplicationUser> signInManager) =>
        {
            if (!status.GoogleAvailable)
            {
                return Results.LocalRedirect("/account/login?error=google_unavailable");
            }

            var callback = $"/account/external-callback?returnUrl={Uri.EscapeDataString(SafeReturnUrl(returnUrl))}";
            var properties = signInManager.ConfigureExternalAuthenticationProperties(AuthProviderStatus.GoogleScheme, callback);
            return Results.Challenge(properties, [AuthProviderStatus.GoogleScheme]);
        });

        // Connect Google to the signed-in account (Account settings).
        group.MapPost("/link/google", async (
            HttpContext http,
            AuthProviderStatus status,
            SignInManager<ApplicationUser> signInManager,
            UserManager<ApplicationUser> userManager) =>
        {
            var user = await userManager.GetUserAsync(http.User);
            if (user is null)
            {
                return Results.LocalRedirect("/account/login?returnUrl=%2Faccount%2Fsettings");
            }

            if (!status.GoogleAvailable)
            {
                return Results.LocalRedirect("/account/settings?status=google_unavailable");
            }

            // Start from a clean external cookie so a stale sign-in can't be linked by mistake.
            await http.SignOutAsync(IdentityConstants.ExternalScheme);
            var properties = signInManager.ConfigureExternalAuthenticationProperties(
                AuthProviderStatus.GoogleScheme,
                "/account/external-callback?mode=link",
                await userManager.GetUserIdAsync(user));
            return Results.Challenge(properties, [AuthProviderStatus.GoogleScheme]);
        });

        group.MapGet("/external-callback", async (
            string? returnUrl,
            string? mode,
            HttpContext http,
            SignInManager<ApplicationUser> signInManager,
            UserManager<ApplicationUser> userManager,
            ExternalAccountService accounts) =>
        {
            var target = SafeReturnUrl(returnUrl);

            if (mode == "link")
            {
                var current = await userManager.GetUserAsync(http.User);
                if (current is null)
                {
                    return Results.LocalRedirect("/account/login?error=external");
                }

                var linkInfo = await signInManager.GetExternalLoginInfoAsync(await userManager.GetUserIdAsync(current));
                await http.SignOutAsync(IdentityConstants.ExternalScheme);
                if (linkInfo is null)
                {
                    return Results.LocalRedirect("/account/settings?status=link_failed");
                }

                var outcome = await accounts.LinkAsync(current, ExternalProfile.FromPrincipal(linkInfo.LoginProvider, linkInfo.ProviderKey, linkInfo.Principal));
                await signInManager.RefreshSignInAsync(current);
                return Results.LocalRedirect(outcome switch
                {
                    ExternalLinkOutcome.Linked or ExternalLinkOutcome.AlreadyLinked => "/account/settings?status=google_linked",
                    ExternalLinkOutcome.LinkedToAnotherAccount => "/account/settings?status=google_in_use",
                    _ => "/account/settings?status=link_failed",
                });
            }

            var info = await signInManager.GetExternalLoginInfoAsync();
            if (info is null)
            {
                return Results.LocalRedirect("/account/login?error=external");
            }

            var (result, user) = await accounts.SignInOrCreateAsync(ExternalProfile.FromPrincipal(info.LoginProvider, info.ProviderKey, info.Principal));
            await http.SignOutAsync(IdentityConstants.ExternalScheme);
            if (user is null)
            {
                var error = result switch
                {
                    ExternalSignInOutcome.EmailInUse => "email_in_use",
                    ExternalSignInOutcome.LockedOut => "locked",
                    _ => "external",
                };
                return Results.LocalRedirect($"/account/login?error={error}&returnUrl={Uri.EscapeDataString(target)}");
            }

            await signInManager.SignInAsync(user, isPersistent: true, authenticationMethod: info.LoginProvider);
            return Results.LocalRedirect(result == ExternalSignInOutcome.AccountCreated && target == "/" ? "/account/settings?status=welcome" : target);
        });

        group.MapPost("/logout", async (SignInManager<ApplicationUser> signInManager, HttpContext http) =>
        {
            await signInManager.SignOutAsync();
            await http.SignOutAsync(IdentityConstants.ExternalScheme);
            return Results.LocalRedirect("/?signedout=1");
        }).WithMetadata(new RequireAntiforgeryTokenAttribute(required: true));

        return app;
    }

    /// <summary>Only local paths are accepted, which prevents open redirects.</summary>
    internal static string SafeReturnUrl(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) &&
        returnUrl.StartsWith('/') &&
        !returnUrl.StartsWith("//", StringComparison.Ordinal) &&
        !returnUrl.StartsWith("/\\", StringComparison.Ordinal) &&
        !returnUrl.Contains('\r', StringComparison.Ordinal) &&
        !returnUrl.Contains('\n', StringComparison.Ordinal)
            ? returnUrl
            : "/";
}
