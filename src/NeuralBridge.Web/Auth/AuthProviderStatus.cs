namespace NeuralBridge.Web.Auth;

/// <summary>
/// The only authentication facts the browser ever sees: which sign-in methods are available.
/// It never contains client IDs, secrets, endpoints or configuration hints.
/// </summary>
public sealed record AuthProviderStatus(bool GoogleAvailable, bool LocalAccountsAvailable, bool PasswordResetAvailable)
{
    public const string GoogleScheme = "Google";

    public const string GoogleUnavailableMessage = "Google sign-in is not configured.";

    /// <summary>Any way to have an account at all.</summary>
    public bool AccountsAvailable => GoogleAvailable || LocalAccountsAvailable;
}
