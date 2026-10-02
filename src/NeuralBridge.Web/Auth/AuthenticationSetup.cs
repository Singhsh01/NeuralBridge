using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using NeuralBridge.Infrastructure.Identity;

namespace NeuralBridge.Web.Auth;

public static class AuthenticationSetup
{
    /// <summary>
    /// Registers ASP.NET Core Identity (always: local accounts and/or Google), Google OAuth when
    /// credentials are configured, cookies, email for password reset, and the safe
    /// <see cref="AuthProviderStatus"/> the UI uses. Guests never need any of it.
    /// </summary>
    public static AuthProviderStatus AddNeuralBridgeAuthentication(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.AddOptions<GoogleAuthOptions>()
            .Bind(configuration.GetSection(GoogleAuthOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(o => !o.IsPartiallyConfigured, "Authentication:Google needs both ClientId and ClientSecret, or neither. Only one of them is set.")
            .ValidateOnStart();
        services.AddOptions<LocalAccountOptions>()
            .Bind(configuration.GetSection(LocalAccountOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<EmailOptions>()
            .Bind(configuration.GetSection(EmailOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(o => o.Mode != EmailMode.Smtp || o.Smtp.IsConfigured, "Email:Mode is Smtp but Email:Smtp:Host is not set.")
            .ValidateOnStart();

        var google = configuration.GetSection(GoogleAuthOptions.SectionName).Get<GoogleAuthOptions>() ?? new GoogleAuthOptions();
        var local = configuration.GetSection(LocalAccountOptions.SectionName).Get<LocalAccountOptions>() ?? new LocalAccountOptions();
        var email = configuration.GetSection(EmailOptions.SectionName).Get<EmailOptions>() ?? new EmailOptions();

        var emailAvailable = RegisterEmailSender(services, email, environment);
        var status = new AuthProviderStatus(
            GoogleAvailable: google.IsConfigured,
            LocalAccountsAvailable: local.Enabled,
            PasswordResetAvailable: local.Enabled && emailAvailable);
        services.AddSingleton(status);

        services.AddCascadingAuthenticationState();
        services.AddAuthorization();
        services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();

        var authentication = services.AddAuthentication(options =>
        {
            options.DefaultScheme = IdentityConstants.ApplicationScheme;
            options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
        });
        authentication.AddIdentityCookies();
        if (google.IsConfigured)
        {
            authentication.AddNeuralBridgeGoogle(google, environment);
        }

        services.AddIdentityCore<ApplicationUser>(options =>
            {
                options.SignIn.RequireConfirmedAccount = false;
                options.User.RequireUniqueEmail = true;
                options.Password.RequiredLength = local.MinimumPasswordLength;
                options.Password.RequireDigit = false;
                options.Password.RequireLowercase = false;
                options.Password.RequireUppercase = false;
                options.Password.RequireNonAlphanumeric = false;
                options.Password.RequiredUniqueChars = 4;
                options.Lockout.AllowedForNewUsers = true;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            })
            .AddSignInManager()
            .AddNeuralBridgeUserStore(configuration)
            .AddDefaultTokenProviders()
            .AddClaimsPrincipalFactory<NeuralBridgeClaimsFactory>();

        services.Configure<DataProtectionTokenProviderOptions>(o => o.TokenLifespan = TimeSpan.FromHours(2));
        services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.FromMinutes(1));

        var cookieSecurity = CookieSecurityFor(configuration, environment);
        services.ConfigureApplicationCookie(options =>
        {
            options.Cookie.Name = "__nb_auth";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax; // required for the OAuth redirect back from Google
            options.Cookie.SecurePolicy = cookieSecurity;
            options.LoginPath = "/account/login";
            options.AccessDeniedPath = "/account/login";
            options.ExpireTimeSpan = TimeSpan.FromDays(14);
            options.SlidingExpiration = true;
        });
        services.ConfigureExternalCookie(options =>
        {
            options.Cookie.Name = "__nb_ext";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = cookieSecurity;
            options.ExpireTimeSpan = TimeSpan.FromMinutes(10);
        });

        services.AddScoped<ExternalAccountService>();
        services.AddHostedService<AuthStartupReport>();
        return status;
    }

    /// <summary><c>Always</c> in production HTTPS deployments; <c>SameAsRequest</c> in Development or when HTTPS redirection is explicitly disabled.</summary>
    public static CookieSecurePolicy CookieSecurityFor(IConfiguration configuration, IHostEnvironment environment)
    {
        var hosting = configuration.GetSection(Infrastructure.HostingOptions.SectionName).Get<Infrastructure.HostingOptions>() ?? new Infrastructure.HostingOptions();
        return environment.IsDevelopment() || !hosting.HttpsRedirection ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
    }

    private static bool RegisterEmailSender(IServiceCollection services, EmailOptions email, IHostEnvironment environment)
    {
        var mode = email.Mode switch
        {
            EmailMode.Auto when email.Smtp.IsConfigured => EmailMode.Smtp,
            EmailMode.Auto when environment.IsDevelopment() || environment.IsEnvironment("Testing") => EmailMode.Outbox,
            EmailMode.Auto => EmailMode.Disabled,
            var explicitMode => explicitMode,
        };

        switch (mode)
        {
            case EmailMode.Smtp:
                services.AddSingleton<IAccountEmailSender, SmtpEmailSender>();
                return true;
            case EmailMode.Outbox:
                services.AddSingleton<OutboxEmailSender>();
                services.AddSingleton<IAccountEmailSender>(sp => sp.GetRequiredService<OutboxEmailSender>());
                return true;
            default:
                services.AddSingleton<IAccountEmailSender, DisabledEmailSender>();
                return false;
        }
    }

    /// <summary>The CSP form-action origins sign-in needs (this site, plus the Google authorization endpoint when enabled).</summary>
    public static string FormActionSources(IOptions<GoogleAuthOptions> google, IHostEnvironment environment)
    {
        if (!google.Value.IsConfigured)
        {
            return "'self'";
        }

        var endpoint = new Uri(GoogleOAuthSetup.EffectiveAuthorizationEndpoint(google.Value, environment));
        return $"'self' {endpoint.GetLeftPart(UriPartial.Authority)}";
    }
}
