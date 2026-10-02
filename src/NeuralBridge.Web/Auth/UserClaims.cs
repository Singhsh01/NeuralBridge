using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using NeuralBridge.Infrastructure.Identity;

namespace NeuralBridge.Web.Auth;

/// <summary>Adds the few profile facts the UI needs on every request, so rendering the header never hits the database.</summary>
public sealed class NeuralBridgeClaimsFactory : UserClaimsPrincipalFactory<ApplicationUser>
{
    public const string DisplayNameClaim = "nb:name";
    public const string AvatarClaim = "nb:avatar";
    public const string KeepHistoryClaim = "nb:history";

    public NeuralBridgeClaimsFactory(UserManager<ApplicationUser> userManager, IOptions<IdentityOptions> optionsAccessor)
        : base(userManager, optionsAccessor)
    {
    }

    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(ApplicationUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        var identity = await base.GenerateClaimsAsync(user);
        identity.AddClaim(new Claim(DisplayNameClaim, UserClaims.FallbackName(user.DisplayName, user.Email)));
        if (user.AvatarUrl is not null)
        {
            identity.AddClaim(new Claim(AvatarClaim, user.AvatarUrl));
        }

        identity.AddClaim(new Claim(KeepHistoryClaim, user.KeepSessionHistory ? "1" : "0"));
        return identity;
    }
}

public static class UserClaims
{
    public static string? GetUserId(this ClaimsPrincipal? user) =>
        user?.Identity?.IsAuthenticated == true ? user.FindFirstValue(ClaimTypes.NameIdentifier) : null;

    public static string DisplayName(this ClaimsPrincipal user) =>
        user.FindFirstValue(NeuralBridgeClaimsFactory.DisplayNameClaim) ?? FallbackName(null, user.FindFirstValue(ClaimTypes.Email));

    public static string? Email(this ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.Email);

    public static string? AvatarUrl(this ClaimsPrincipal user) => user.FindFirstValue(NeuralBridgeClaimsFactory.AvatarClaim);

    public static bool KeepsSessionHistory(this ClaimsPrincipal user) => user.FindFirstValue(NeuralBridgeClaimsFactory.KeepHistoryClaim) != "0";

    public static string FallbackName(string? displayName, string? email) =>
        !string.IsNullOrWhiteSpace(displayName) ? displayName
        : !string.IsNullOrWhiteSpace(email) ? email.Split('@')[0]
        : "Your account";

    /// <summary>One or two letters for the avatar badge.</summary>
    public static string Initials(string name)
    {
        var parts = (name ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var letters = parts.Length switch
        {
            0 => "?",
            1 => parts[0][..1],
            _ => string.Concat(parts[0][..1], parts[^1][..1]),
        };
        return letters.ToUpper(CultureInfo.CurrentCulture);
    }

    /// <summary>A stable hue (0–359) per user, so avatars are recognisable without a picture.</summary>
    public static int AvatarHue(string seed) =>
        (int)((uint)(seed ?? string.Empty).Aggregate(17, (h, c) => unchecked((h * 31) + c)) % 360);
}
