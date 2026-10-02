using NeuralBridge.Domain.Documents;
using NeuralBridge.Domain.Sessions;

namespace NeuralBridge.Application.Abstractions;

public interface ISessionCodeGenerator
{
    /// <summary>A new join code drawn from a cryptographically secure RNG.</summary>
    SessionCode Generate();

    /// <summary>A new 128-bit, URL-safe public identifier for workspace URLs.</summary>
    string GeneratePublicId();
}

/// <summary>Slow, salted hashing for low-entropy secrets (PINs/passwords).</summary>
public interface ISecretHasher
{
    string Hash(string secret);

    bool Verify(string secret, string encodedHash);
}

/// <summary>High-entropy bearer tokens; only their fast hash is persisted.</summary>
public interface ITokenService
{
    string GenerateToken();

    string HashToken(string token);

    /// <summary>Constant-time comparison of a presented token against a stored hash.</summary>
    bool Matches(string? token, string storedHash);
}

public enum RateLimitPolicy
{
    CreateSession,
    JoinSession,
    DocumentUpdate,
    LocationLookup,
    Authentication,
    CallSignal,
}

public interface IAppRateLimiter
{
    /// <summary>Attempts to take one permit for <paramref name="partitionKey"/>; returns <c>false</c> when limited.</summary>
    bool TryAcquire(RateLimitPolicy policy, string partitionKey);
}

/// <summary>Volatile storage for live document text. Nothing here survives a restart, by design.</summary>
public interface IDocumentStore
{
    SharedDocument GetOrCreate(Guid sessionId, DateTimeOffset now);

    /// <summary>Atomically replaces the document using <paramref name="update"/>, which receives the current value.</summary>
    TResult Update<TResult>(Guid sessionId, DateTimeOffset now, Func<SharedDocument, (SharedDocument Next, TResult Result)> update);

    bool Remove(Guid sessionId);

    int Count { get; }

    /// <summary>Snapshot of session ids that currently have a document (used to purge orphans).</summary>
    IReadOnlyCollection<Guid> SessionIds { get; }
}

/// <summary>Tracks which participants currently have live connections (Blazor circuits or hub connections).</summary>
public interface IPresenceTracker
{
    /// <summary>Returns <c>true</c> when the participant transitioned from offline to online.</summary>
    bool Connect(Guid sessionId, Guid participantId, string connectionId);

    /// <summary>Returns <c>true</c> when the participant transitioned from online to offline.</summary>
    bool Disconnect(Guid sessionId, Guid participantId, string connectionId);

    IReadOnlySet<Guid> GetOnlineParticipants(Guid sessionId);

    void ClearSession(Guid sessionId);
}

/// <summary>Serializes mutations of a single session within this process.</summary>
public interface ISessionLockProvider
{
    Task<IAsyncDisposable> AcquireAsync(Guid sessionId, CancellationToken cancellationToken = default);
}

public sealed record GeoPlace(string? City, string? Region, string? Country, string? CountryCode);

public interface IReverseGeocoder
{
    /// <summary>Returns <c>null</c> when the lookup is disabled, fails or finds nothing.</summary>
    Task<GeoPlace?> ReverseAsync(double latitude, double longitude, CancellationToken cancellationToken = default);
}
