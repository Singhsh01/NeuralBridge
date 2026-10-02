namespace NeuralBridge.Web.Infrastructure;

/// <summary>Bound from <c>NeuralBridge:Hosting</c>.</summary>
public sealed class HostingOptions
{
    public const string SectionName = "NeuralBridge:Hosting";

    /// <summary>Redirect HTTP to HTTPS. Disable when TLS terminates at a reverse proxy, or for LAN/dev over HTTP.</summary>
    public bool HttpsRedirection { get; set; } = true;

    /// <summary>
    /// Honour X-Forwarded-For/Proto. Enable <b>only</b> behind a trusted reverse proxy:
    /// these headers decide the client IP that rate limits are keyed on.
    /// </summary>
    public bool ForwardedHeadersEnabled { get; set; }

    /// <summary>Directory for ASP.NET Data Protection keys (protects auth cookies and stored participant tokens). Use a persistent volume in containers.</summary>
    public string? DataProtectionKeysPath { get; set; }
}
