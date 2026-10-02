using System.ComponentModel.DataAnnotations;

namespace NeuralBridge.Application.Options;

/// <summary>
/// Bound from <c>NeuralBridge:RateLimits</c>. These application-level limits also apply to
/// actions performed over a Blazor circuit, which HTTP middleware never sees.
/// </summary>
public sealed class RateLimitOptions
{
    public const string SectionName = "NeuralBridge:RateLimits";

    /// <summary>Session creations per client per minute.</summary>
    [Range(1, 10_000)]
    public int CreateSessionPerMinute { get; set; } = 10;

    /// <summary>Join attempts (valid or not) per client per minute.</summary>
    [Range(1, 10_000)]
    public int JoinSessionPerMinute { get; set; } = 20;

    /// <summary>Document updates per participant per second (token bucket, burst = this value).</summary>
    [Range(1, 1000)]
    public int DocumentUpdatesPerSecond { get; set; } = 10;

    /// <summary>Reverse-geocoding lookups per client per minute.</summary>
    [Range(1, 1000)]
    public int LocationLookupsPerMinute { get; set; } = 5;

    /// <summary>Sign-in, registration and password-reset attempts per client per minute.</summary>
    [Range(1, 1000)]
    public int AuthenticationAttemptsPerMinute { get; set; } = 10;

    /// <summary>Call-signaling messages (WebRTC offers/answers/candidates) per participant per second.</summary>
    [Range(5, 1000)]
    public int CallSignalsPerSecond { get; set; } = 60;

    /// <summary>Generic HTTP requests per client IP per minute (middleware).</summary>
    [Range(10, 100_000)]
    public int HttpRequestsPerMinute { get; set; } = 600;
}
