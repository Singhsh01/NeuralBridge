using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NeuralBridge.Infrastructure.Identity;
using NeuralBridge.Web.Auth;

namespace NeuralBridge.Tests.Web;

internal sealed class TestHostEnvironment(string name) : IHostEnvironment
{
    public string EnvironmentName { get; set; } = name;

    public string ApplicationName { get; set; } = "NeuralBridge.Web";

    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}

internal static class AuthHost
{
    public const string ClientId = "unit-test-client.apps.googleusercontent.com";
    public const string ClientSecret = "unit-test-secret-value";

    public static ServiceProvider Build(string environment, Dictionary<string, string?> settings, out AuthProviderStatus status)
    {
        var values = new Dictionary<string, string?> { ["NeuralBridge:Persistence:Provider"] = "InMemory" };
        foreach (var (k, v) in settings)
        {
            values[k] = v;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton(TimeProvider.System);
        services.AddDataProtection();
        status = services.AddNeuralBridgeAuthentication(configuration, new TestHostEnvironment(environment));
        return services.BuildServiceProvider();
    }

    public static Dictionary<string, string?> Google(Dictionary<string, string?>? extra = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["Authentication:Google:ClientId"] = ClientId,
            ["Authentication:Google:ClientSecret"] = ClientSecret,
        };
        foreach (var (k, v) in extra ?? [])
        {
            values[k] = v;
        }

        return values;
    }
}

public class AuthProviderStatusTests
{
    [Fact]
    public async Task Google_is_unavailable_without_credentials_and_guests_still_work()
    {
        using var sp = AuthHost.Build("Production", [], out var status);
        Assert.False(status.GoogleAvailable);
        Assert.True(status.LocalAccountsAvailable);
        Assert.False(status.PasswordResetAvailable); // no SMTP in production => no reset emails
        // No Google handler is registered, so a challenge can't reach a half-configured provider.
        var schemes = sp.GetRequiredService<Microsoft.AspNetCore.Authentication.IAuthenticationSchemeProvider>();
        Assert.Null(await schemes.GetSchemeAsync(AuthProviderStatus.GoogleScheme));
    }

    [Fact]
    public async Task Google_is_available_when_both_values_are_configured()
    {
        using var sp = AuthHost.Build("Production", AuthHost.Google(), out var status);
        Assert.True(status.GoogleAvailable);
        var schemes = sp.GetRequiredService<Microsoft.AspNetCore.Authentication.IAuthenticationSchemeProvider>();
        Assert.NotNull(await schemes.GetSchemeAsync(AuthProviderStatus.GoogleScheme));
    }

    [Theory]
    [InlineData("Authentication:Google:ClientId")]
    [InlineData("Authentication:Google:ClientSecret")]
    public void Half_configured_google_fails_validation_without_echoing_values(string onlyKey)
    {
        var settings = new Dictionary<string, string?> { [onlyKey] = "only-this-one-is-set-123" };
        using var sp = AuthHost.Build("Production", settings, out var status);
        Assert.False(status.GoogleAvailable);
        var ex = Assert.Throws<OptionsValidationException>(() => sp.GetRequiredService<IOptions<GoogleAuthOptions>>().Value);
        Assert.DoesNotContain("only-this-one-is-set-123", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_public_status_payload_never_contains_credentials()
    {
        using var sp = AuthHost.Build("Development", AuthHost.Google(), out var status);
        var json = JsonSerializer.Serialize(new { google = status.GoogleAvailable, localAccounts = status.LocalAccountsAvailable, passwordReset = status.PasswordResetAvailable });
        Assert.DoesNotContain(AuthHost.ClientId, json, StringComparison.Ordinal);
        Assert.DoesNotContain(AuthHost.ClientSecret, json, StringComparison.Ordinal);
        Assert.DoesNotContain(AuthHost.ClientSecret, JsonSerializer.Serialize(status), StringComparison.Ordinal);
    }
}

public class GoogleOAuthConfigurationTests
{
    private static OAuthOptions OAuth(string environment, Dictionary<string, string?>? extra = null)
    {
        var sp = AuthHost.Build(environment, AuthHost.Google(extra), out _);
        return sp.GetRequiredService<IOptionsMonitor<OAuthOptions>>().Get(AuthProviderStatus.GoogleScheme);
    }

    [Fact]
    public void Production_uses_google_endpoints_pkce_and_the_configured_callback()
    {
        var o = OAuth("Production");
        Assert.Equal(GoogleOAuthSetup.AuthorizationEndpoint, o.AuthorizationEndpoint);
        Assert.Equal(GoogleOAuthSetup.TokenEndpoint, o.TokenEndpoint);
        Assert.Equal(GoogleOAuthSetup.UserInformationEndpoint, o.UserInformationEndpoint);
        Assert.True(o.UsePkce);
        Assert.Equal("/signin-google", o.CallbackPath.Value);
        Assert.Contains("openid", o.Scope);
        Assert.Contains("email", o.Scope);
        Assert.Contains("profile", o.Scope);
        Assert.Equal(AuthHost.ClientId, o.ClientId);
    }

    [Fact]
    public void Callback_path_comes_from_configuration()
    {
        var o = OAuth("Production", new() { ["Authentication:Google:CallbackPath"] = "/auth/google/callback" });
        Assert.Equal("/auth/google/callback", o.CallbackPath.Value);
    }

    [Fact]
    public void Test_endpoint_overrides_are_ignored_outside_development_and_testing()
    {
        var overrides = new Dictionary<string, string?>
        {
            ["Authentication:Google:TestEndpoints:AuthorizationEndpoint"] = "http://127.0.0.1:5399/authorize",
            ["Authentication:Google:TestEndpoints:TokenEndpoint"] = "http://127.0.0.1:5399/token",
            ["Authentication:Google:TestEndpoints:UserInformationEndpoint"] = "http://127.0.0.1:5399/userinfo",
        };
        var production = OAuth("Production", overrides);
        Assert.Equal(GoogleOAuthSetup.AuthorizationEndpoint, production.AuthorizationEndpoint);
        Assert.Equal(GoogleOAuthSetup.TokenEndpoint, production.TokenEndpoint);

        var testing = OAuth("Testing", overrides);
        Assert.Equal("http://127.0.0.1:5399/authorize", testing.AuthorizationEndpoint);
        Assert.Equal("http://127.0.0.1:5399/token", testing.TokenEndpoint);
    }

    [Fact]
    public void Csp_form_action_allows_only_self_and_the_active_authorization_origin()
    {
        var env = new TestHostEnvironment("Production");
        Assert.Equal("'self'", AuthenticationSetup.FormActionSources(Microsoft.Extensions.Options.Options.Create(new GoogleAuthOptions()), env));
        var configured = new GoogleAuthOptions { ClientId = AuthHost.ClientId, ClientSecret = AuthHost.ClientSecret };
        Assert.Equal("'self' https://accounts.google.com", AuthenticationSetup.FormActionSources(Microsoft.Extensions.Options.Options.Create(configured), env));
    }

    [Theory]
    [InlineData("http://127.0.0.1:5243", "http://127.0.0.1:5243/signin-google")]
    [InlineData("https://localhost:7243/", "https://localhost:7243/signin-google")]
    [InlineData("http://0.0.0.0:8080", "http://localhost:8080/signin-google")]
    [InlineData("http://[::]:8080", "http://localhost:8080/signin-google")]
    [InlineData("http://+:5243", "http://localhost:5243/signin-google")]
    public void Startup_report_builds_redirect_uris_from_listen_addresses(string address, string expected)
    {
        Assert.Equal([expected], AuthStartupReport.RedirectUris([address], "/signin-google"));
    }
}

public class ExternalAccountServiceTests
{
    private static (ServiceProvider Sp, UserManager<ApplicationUser> Users, ExternalAccountService Service) Create()
    {
        var sp = AuthHost.Build("Testing", [], out _);
        var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
        return (sp, users, new ExternalAccountService(users, TimeProvider.System, NullLogger<ExternalAccountService>.Instance));
    }

    private static ExternalProfile Ada(string key = "google-sub-1", string? email = "ada@example.test") =>
        new("Google", key, email, true, "Ada Lovelace", "https://lh3.googleusercontent.com/a/ada");

    [Fact]
    public async Task First_sign_in_creates_a_local_account_with_a_login()
    {
        var (sp, users, service) = Create();
        using var _ = sp;
        var (outcome, user) = await service.SignInOrCreateAsync(Ada());
        Assert.Equal(ExternalSignInOutcome.AccountCreated, outcome);
        Assert.NotNull(user);
        Assert.Equal("Ada Lovelace", user!.DisplayName);
        Assert.True(user.EmailConfirmed);
        Assert.Equal(user.Id, (await users.FindByLoginAsync("Google", "google-sub-1"))!.Id);
        Assert.False(await users.HasPasswordAsync(user));
    }

    [Fact]
    public async Task Returning_user_is_matched_by_provider_key_and_keeps_their_chosen_name()
    {
        var (sp, users, service) = Create();
        using var _ = sp;
        var (_, user) = await service.SignInOrCreateAsync(Ada());
        user!.DisplayName = "Countess";
        await users.UpdateAsync(user);

        var (outcome, again) = await service.SignInOrCreateAsync(Ada() with { DisplayName = "Ada L.", AvatarUrl = "https://lh3.googleusercontent.com/a/new" });
        Assert.Equal(ExternalSignInOutcome.ExistingAccount, outcome);
        Assert.Equal(user.Id, again!.Id);
        Assert.Equal("Countess", again.DisplayName);
        Assert.Equal("https://lh3.googleusercontent.com/a/new", again.AvatarUrl);
    }

    [Fact]
    public async Task Existing_password_account_with_the_same_email_is_never_linked_automatically()
    {
        var (sp, users, service) = Create();
        using var _ = sp;
        var local = new ApplicationUser { UserName = "ada-local", Email = "ada@example.test" };
        Assert.True((await users.CreateAsync(local, "correct horse battery")).Succeeded);

        var (outcome, user) = await service.SignInOrCreateAsync(Ada());
        Assert.Equal(ExternalSignInOutcome.EmailInUse, outcome);
        Assert.Null(user);
        Assert.Empty(await users.GetLoginsAsync(local));
    }

    [Fact]
    public async Task Linking_is_explicit_and_one_google_identity_cannot_serve_two_accounts()
    {
        var (sp, users, service) = Create();
        using var _ = sp;
        var first = new ApplicationUser { UserName = "first", Email = "first@example.test" };
        var second = new ApplicationUser { UserName = "second", Email = "second@example.test" };
        await users.CreateAsync(first, "correct horse battery");
        await users.CreateAsync(second, "correct horse battery");

        Assert.Equal(ExternalLinkOutcome.Linked, await service.LinkAsync(first, Ada("sub-x", "first@example.test")));
        Assert.Equal(ExternalLinkOutcome.AlreadyLinked, await service.LinkAsync(first, Ada("sub-x")));
        Assert.Equal(ExternalLinkOutcome.LinkedToAnotherAccount, await service.LinkAsync(second, Ada("sub-x")));
    }

    [Fact]
    public async Task Locked_out_accounts_cannot_sign_in_with_google()
    {
        var (sp, users, service) = Create();
        using var _ = sp;
        var (_, user) = await service.SignInOrCreateAsync(Ada());
        await users.SetLockoutEndDateAsync(user!, DateTimeOffset.UtcNow.AddMinutes(10));
        var (outcome, signedIn) = await service.SignInOrCreateAsync(Ada());
        Assert.Equal(ExternalSignInOutcome.LockedOut, outcome);
        Assert.Null(signedIn);
    }

    [Theory]
    [InlineData("https://lh3.googleusercontent.com/a/abc", true)]
    [InlineData("http://lh3.googleusercontent.com/a/abc", false)]
    [InlineData("https://evil.example/a.png", false)]
    [InlineData("https://googleusercontent.com.evil.example/a.png", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData(null, false)]
    public void Only_google_hosted_https_avatars_are_kept(string? url, bool kept)
    {
        Assert.Equal(kept, ExternalProfile.SafeAvatarUrl(url) is not null);
    }

    [Fact]
    public void Profile_is_read_from_the_validated_principal_and_cleaned()
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Email, "  ada@example.test "),
            new Claim(ClaimTypes.Name, "  Ada\u0000 Lovelace  "),
            new Claim(GoogleOAuthSetup.EmailVerifiedClaim, "true"),
            new Claim(GoogleOAuthSetup.PictureClaim, "https://evil.example/x.png"),
        ], "Google");
        var profile = ExternalProfile.FromPrincipal("Google", "sub-1", new ClaimsPrincipal(identity));
        Assert.Equal("ada@example.test", profile.Email);
        Assert.True(profile.EmailVerified);
        Assert.Null(profile.AvatarUrl);
        Assert.DoesNotContain("\u0000", profile.DisplayName!, StringComparison.Ordinal);
        Assert.StartsWith("Ada", profile.DisplayName!, StringComparison.Ordinal);
    }
}
