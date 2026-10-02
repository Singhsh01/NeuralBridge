using System.ComponentModel.DataAnnotations;

namespace NeuralBridge.Web.Auth;

/// <summary>
/// Bound from <c>Authentication:Google</c>. The values come from user secrets (development),
/// environment variables (<c>Authentication__Google__ClientId</c>), key-per-file secrets
/// (Docker/Kubernetes) or any other configuration provider. Never from appsettings in source control.
/// </summary>
public sealed class GoogleAuthOptions
{
    public const string SectionName = "Authentication:Google";

    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    /// <summary>Path Google redirects back to. The redirect URI to register is <c>{public base URL}{CallbackPath}</c>.</summary>
    [RegularExpression("^/[A-Za-z0-9/_-]+$")]
    public string CallbackPath { get; set; } = "/signin-google";

    /// <summary>When true and credentials are missing, a warning is logged at startup (the app still starts; guests are unaffected).</summary>
    public bool ExpectedInProduction { get; set; }

    /// <summary>
    /// Testing only: alternative OAuth endpoints (a mock provider for automated tests).
    /// Ignored unless the environment is Development or Testing.
    /// </summary>
    public GoogleEndpointOverrides? TestEndpoints { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);

    public bool IsPartiallyConfigured => string.IsNullOrWhiteSpace(ClientId) != string.IsNullOrWhiteSpace(ClientSecret);
}

public sealed class GoogleEndpointOverrides
{
    public string? AuthorizationEndpoint { get; set; }

    public string? TokenEndpoint { get; set; }

    public string? UserInformationEndpoint { get; set; }
}

/// <summary>Bound from <c>Authentication:LocalAccounts</c>.</summary>
public sealed class LocalAccountOptions
{
    public const string SectionName = "Authentication:LocalAccounts";

    /// <summary>Allow email + password registration and sign-in.</summary>
    public bool Enabled { get; set; } = true;

    [Range(8, 128)]
    public int MinimumPasswordLength { get; set; } = 10;
}

public enum EmailMode
{
    /// <summary>Development: messages go to an in-app outbox at /dev/outbox. Production: password reset by email is unavailable.</summary>
    Auto,
    Smtp,
    Outbox,
    Disabled,
}

/// <summary>Bound from <c>Email</c>. Used for password-reset messages.</summary>
public sealed class EmailOptions
{
    public const string SectionName = "Email";

    public EmailMode Mode { get; set; } = EmailMode.Auto;

    public string? From { get; set; }

    public SmtpOptions Smtp { get; set; } = new();
}

public sealed class SmtpOptions
{
    public string? Host { get; set; }

    [Range(1, 65535)]
    public int Port { get; set; } = 587;

    public bool EnableSsl { get; set; } = true;

    public string? Username { get; set; }

    public string? Password { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host);
}
