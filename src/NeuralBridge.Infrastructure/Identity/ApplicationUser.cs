using Microsoft.AspNetCore.Identity;

namespace NeuralBridge.Infrastructure.Identity;

/// <summary>Identity user for optional accounts (local password and/or Google).</summary>
public sealed class ApplicationUser : IdentityUser
{
    [PersonalData]
    public string? DisplayName { get; set; }

    /// <summary>Profile picture URL from the external provider (Google), when available.</summary>
    [PersonalData]
    public string? AvatarUrl { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? LastSignInAt { get; set; }

    /// <summary>Keep metadata-only history of ended sessions in "My sessions".</summary>
    public bool KeepSessionHistory { get; set; } = true;
}
