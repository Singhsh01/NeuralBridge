using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NeuralBridge.Application;
using NeuralBridge.Application.Common;
using NeuralBridge.Application.Options;
using NeuralBridge.Web.Api;
using NeuralBridge.Web.Auth;
using NeuralBridge.Web.Components.Session;

namespace NeuralBridge.Tests.Web;

public class LinkDetectorTests
{
    [Fact]
    public void Finds_only_http_and_https_links()
    {
        var links = LinkDetector.Find("see https://example.org/a?b=1, http://intranet.local/x and javascript:alert(1) or data:text/html,hi or ftp://f.example");
        Assert.Equal(new[] { "https://example.org/a?b=1", "http://intranet.local/x" }, links.Select(l => l.AbsoluteUri).ToArray());
    }

    [Fact]
    public void Trims_trailing_punctuation_and_deduplicates()
    {
        var links = LinkDetector.Find("(https://example.org/docs). Again: https://example.org/docs!");
        Assert.Equal("https://example.org/docs", Assert.Single(links).AbsoluteUri);
    }

    [Fact]
    public void Caps_the_number_of_links()
    {
        var text = string.Join(' ', Enumerable.Range(0, 50).Select(i => $"https://e{i}.example"));
        Assert.Equal(LinkDetector.MaxLinks, LinkDetector.Find(text).Count);
    }
}

public class ReturnUrlTests
{
    [Theory]
    [InlineData("/account/sessions", "/account/sessions")]
    [InlineData("/session/abc", "/session/abc")]
    [InlineData("https://evil.example", "/")]
    [InlineData("//evil.example", "/")]
    [InlineData("/\\evil.example", "/")]
    [InlineData("/%0d%0aSet-Cookie:x", "/%0d%0aSet-Cookie:x")]
    [InlineData("/ok\r\nLocation: https://evil.example", "/")]
    [InlineData("javascript:alert(1)", "/")]
    [InlineData(null, "/")]
    public void Only_local_return_urls_are_accepted(string? input, string expected)
    {
        Assert.Equal(expected, AccountEndpoints.SafeReturnUrl(input));
    }
}

public class ApiStatusMappingTests
{
    [Theory]
    [InlineData(SessionError.InvalidCode, 400)]
    [InlineData(SessionError.NotFoundOrPinIncorrect, 404)]
    [InlineData(SessionError.Expired, 410)]
    [InlineData(SessionError.Closed, 410)]
    [InlineData(SessionError.CapacityReached, 409)]
    [InlineData(SessionError.PinLocked, 423)]
    [InlineData(SessionError.RateLimited, 429)]
    public void Errors_map_to_http_status_codes(SessionError error, int status)
    {
        Assert.Equal(status, SessionApi.StatusFor(error));
    }
}

public class OptionsBindingTests
{
    private static SessionOptions Bind(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection().AddLogging().AddNeuralBridgeApplication(configuration);
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<SessionOptions>>().Value;
    }

    [Fact]
    public void Configured_lists_replace_defaults_instead_of_appending()
    {
        // Regression: binding appended configured array values to non-empty property defaults.
        var options = Bind(new()
        {
            ["NeuralBridge:Sessions:AllowedLifetimesMinutes:0"] = "15",
            ["NeuralBridge:Sessions:AllowedLifetimesMinutes:1"] = "60",
            ["NeuralBridge:Sessions:ExtensionStepsMinutes:0"] = "30",
        });

        Assert.Equal(new[] { 15, 60 }, options.AllowedLifetimesMinutes);
        Assert.Equal(new[] { 30 }, options.ExtensionStepsMinutes);
    }

    [Fact]
    public void Missing_lists_fall_back_to_defaults()
    {
        var options = Bind([]);
        Assert.Equal(new[] { 15, 60, 480, 1440 }, options.AllowedLifetimesMinutes);
        Assert.Equal(new[] { 15, 60 }, options.ExtensionStepsMinutes);
    }

    [Fact]
    public void Invalid_configuration_fails_validation()
    {
        Assert.Throws<OptionsValidationException>(() => Bind(new()
        {
            ["NeuralBridge:Sessions:DefaultLifetimeMinutes"] = "45", // not an offered lifetime
        }));
    }
}

public class GlobalRateLimitScopeTests
{
    [Theory]
    [InlineData("/_framework/blazor.web.abc.js", true)]
    [InlineData("/_blazor/negotiate", true)]
    [InlineData("/img/wallpaper/wallpaper-landscape-1920.x.avif", true)]
    [InlineData("/icons/lucide.svg", true)]
    [InlineData("/Components/Layout/ReconnectModal.abc.razor.js", true)]
    [InlineData("/", false)]
    [InlineData("/account/login", false)]
    [InlineData("/api/sessions/join", false)]
    [InlineData("/session/abc", false)]
    public void Only_static_assets_and_the_circuit_transport_skip_the_page_budget(string path, bool uncounted)
    {
        Assert.Equal(uncounted, NeuralBridge.Web.Infrastructure.WebApplicationSetup.IsUncountedPath(new Microsoft.AspNetCore.Http.PathString(path)));
    }
}
