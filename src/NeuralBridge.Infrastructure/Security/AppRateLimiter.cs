using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;
using NeuralBridge.Application.Abstractions;
using NeuralBridge.Application.Options;

namespace NeuralBridge.Infrastructure.Security;

/// <summary>
/// Partitioned in-memory limiters built on <see cref="System.Threading.RateLimiting"/>.
/// Idle partitions are reclaimed automatically by the partitioned limiter.
/// </summary>
public sealed class AppRateLimiter : IAppRateLimiter, IDisposable
{
    private readonly Dictionary<RateLimitPolicy, PartitionedRateLimiter<string>> _limiters;

    public AppRateLimiter(IOptions<RateLimitOptions> options)
    {
        var o = options.Value;
        _limiters = new Dictionary<RateLimitPolicy, PartitionedRateLimiter<string>>
        {
            [RateLimitPolicy.CreateSession] = PerMinute(o.CreateSessionPerMinute),
            [RateLimitPolicy.JoinSession] = PerMinute(o.JoinSessionPerMinute),
            [RateLimitPolicy.LocationLookup] = PerMinute(o.LocationLookupsPerMinute),
            [RateLimitPolicy.Authentication] = PerMinute(o.AuthenticationAttemptsPerMinute),
            [RateLimitPolicy.CallSignal] = PerSecondBucket(o.CallSignalsPerSecond),
            [RateLimitPolicy.DocumentUpdate] = PerSecondBucket(o.DocumentUpdatesPerSecond),
        };
    }

    public bool TryAcquire(RateLimitPolicy policy, string partitionKey)
    {
        using var lease = _limiters[policy].AttemptAcquire(string.IsNullOrEmpty(partitionKey) ? "unknown" : partitionKey);
        return lease.IsAcquired;
    }

    public void Dispose()
    {
        foreach (var limiter in _limiters.Values)
        {
            limiter.Dispose();
        }
    }

    private static PartitionedRateLimiter<string> PerSecondBucket(int perSecond) =>
        PartitionedRateLimiter.Create<string, string>(key =>
            RateLimitPartition.GetTokenBucketLimiter(key, _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = perSecond,
                TokensPerPeriod = perSecond,
                ReplenishmentPeriod = TimeSpan.FromSeconds(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            }));

    private static PartitionedRateLimiter<string> PerMinute(int permits) =>
        PartitionedRateLimiter.Create<string, string>(key =>
            RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permits,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            }));
}
