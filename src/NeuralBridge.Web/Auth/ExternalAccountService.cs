using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using NeuralBridge.Infrastructure.Identity;

namespace NeuralBridge.Web.Auth;

/// <summary>The identity facts NeuralBridge keeps from an external provider.</summary>
public sealed record ExternalProfile(string Provider, string ProviderKey, string? Email, bool EmailVerified, string? DisplayName, string? AvatarUrl)
{
    public static ExternalProfile FromPrincipal(string provider, string providerKey, ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var email = principal.FindFirstValue(ClaimTypes.Email);
        var name = principal.FindFirstValue(ClaimTypes.Name) ?? principal.FindFirstValue(ClaimTypes.GivenName);
        return new ExternalProfile(
            provider,
            providerKey,
            string.IsNullOrWhiteSpace(email) ? null : email.Trim(),
            principal.FindFirstValue(GoogleOAuthSetup.EmailVerifiedClaim) == "true",
            AccountText.CleanDisplayName(name),
            SafeAvatarUrl(principal.FindFirstValue(GoogleOAuthSetup.PictureClaim)));
    }

    /// <summary>Only Google-hosted https avatars are kept (they are allowed by the CSP img-src list).</summary>
    public static string? SafeAvatarUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        uri.Host.EndsWith(".googleusercontent.com", StringComparison.OrdinalIgnoreCase) &&
        url!.Length <= 1024
            ? uri.AbsoluteUri
            : null;
}

public enum ExternalSignInOutcome
{
    ExistingAccount,
    AccountCreated,

    /// <summary>A local account already uses this email. It is never linked automatically (account-takeover protection).</summary>
    EmailInUse,
    LockedOut,
    Failed,
}

public enum ExternalLinkOutcome
{
    Linked,
    AlreadyLinked,
    LinkedToAnotherAccount,
    Failed,
}

/// <summary>
/// Creates or updates local Identity users from validated external identities. The
/// HTTP-specific steps (cookies, redirects) live in <see cref="AccountEndpoints"/>; this class
/// holds the decisions so they can be unit tested.
/// </summary>
public sealed class ExternalAccountService
{
    private readonly UserManager<ApplicationUser> _users;
    private readonly TimeProvider _time;
    private readonly ILogger<ExternalAccountService> _logger;

    public ExternalAccountService(UserManager<ApplicationUser> users, TimeProvider time, ILogger<ExternalAccountService> logger)
    {
        _users = users;
        _time = time;
        _logger = logger;
    }

    public async Task<(ExternalSignInOutcome Outcome, ApplicationUser? User)> SignInOrCreateAsync(ExternalProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var now = _time.GetUtcNow();
        var existing = await _users.FindByLoginAsync(profile.Provider, profile.ProviderKey);
        if (existing is not null)
        {
            if (await _users.IsLockedOutAsync(existing))
            {
                return (ExternalSignInOutcome.LockedOut, null);
            }

            // Keep the provider's picture current; never overwrite a display name the user chose.
            existing.AvatarUrl = profile.AvatarUrl ?? existing.AvatarUrl;
            existing.DisplayName ??= profile.DisplayName;
            existing.LastSignInAt = now;
            await _users.UpdateAsync(existing);
            return (ExternalSignInOutcome.ExistingAccount, existing);
        }

        if (profile.Email is not null && await _users.FindByEmailAsync(profile.Email) is not null)
        {
            _logger.LogInformation("External sign-in matched an existing account's email; not linking automatically");
            return (ExternalSignInOutcome.EmailInUse, null);
        }

        var user = new ApplicationUser
        {
            UserName = $"{profile.Provider.ToLowerInvariant()}-{profile.ProviderKey}",
            Email = profile.Email,
            EmailConfirmed = profile.Email is not null && profile.EmailVerified,
            DisplayName = profile.DisplayName,
            AvatarUrl = profile.AvatarUrl,
            CreatedAt = now,
            LastSignInAt = now,
        };

        var created = await _users.CreateAsync(user);
        if (created.Succeeded)
        {
            created = await _users.AddLoginAsync(user, new UserLoginInfo(profile.Provider, profile.ProviderKey, profile.Provider));
        }

        if (!created.Succeeded)
        {
            _logger.LogWarning("Creating an account from an external sign-in failed: {Codes}", string.Join(",", created.Errors.Select(e => e.Code)));
            return (ExternalSignInOutcome.Failed, null);
        }

        _logger.LogInformation("Account created from {Provider} sign-in", profile.Provider);
        return (ExternalSignInOutcome.AccountCreated, user);
    }

    public async Task<ExternalLinkOutcome> LinkAsync(ApplicationUser user, ExternalProfile profile)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(profile);
        var owner = await _users.FindByLoginAsync(profile.Provider, profile.ProviderKey);
        if (owner is not null)
        {
            return owner.Id == user.Id ? ExternalLinkOutcome.AlreadyLinked : ExternalLinkOutcome.LinkedToAnotherAccount;
        }

        var result = await _users.AddLoginAsync(user, new UserLoginInfo(profile.Provider, profile.ProviderKey, profile.Provider));
        if (!result.Succeeded)
        {
            return ExternalLinkOutcome.Failed;
        }

        user.AvatarUrl ??= profile.AvatarUrl;
        await _users.UpdateAsync(user);
        return ExternalLinkOutcome.Linked;
    }
}

public static class AccountText
{
    public const int MaxDisplayNameLength = 60;

    public static string? CleanDisplayName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var cleaned = string.Join(' ', new string(name.Where(c => !char.IsControl(c)).ToArray()).Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return cleaned.Length == 0 ? null : cleaned.Length > MaxDisplayNameLength ? cleaned[..MaxDisplayNameLength] : cleaned;
    }
}
