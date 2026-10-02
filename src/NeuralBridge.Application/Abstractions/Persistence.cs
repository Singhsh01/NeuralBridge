using NeuralBridge.Domain.Sessions;
using NeuralBridge.Domain.Snippets;

namespace NeuralBridge.Application.Abstractions;

/// <summary>Creates short-lived units of work. Long-lived Blazor circuits must never hold a database context.</summary>
public interface ISessionStore
{
    ISessionUnitOfWork Begin();
}

/// <summary>
/// One consistent read-modify-write cycle over sessions. Loaded aggregates include their
/// participants. Disposing without <see cref="SaveChangesAsync"/> discards changes.
/// </summary>
public interface ISessionUnitOfWork : IAsyncDisposable
{
    Task<SharedSession?> FindByIdAsync(Guid sessionId, CancellationToken cancellationToken = default);

    Task<SharedSession?> FindByPublicIdAsync(string publicId, CancellationToken cancellationToken = default);

    Task<SharedSession?> FindByCodeAsync(string normalizedCode, CancellationToken cancellationToken = default);

    Task<bool> CodeInUseAsync(string normalizedCode, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SharedSession>> ListActiveByOwnerAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>Sessions still marked active whose expiry is at or before <paramref name="now"/>.</summary>
    Task<IReadOnlyList<SharedSession>> ListOverdueAsync(DateTimeOffset now, int maxCount, CancellationToken cancellationToken = default);

    /// <summary>Every session (any status) owned by a signed-in user.</summary>
    Task<IReadOnlyList<SharedSession>> ListByOwnerAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Ended sessions eligible for deletion: those without retained history that ended before
    /// <paramref name="guestCutoff"/>, and those with retained history that ended before <paramref name="historyCutoff"/>.
    /// </summary>
    Task<IReadOnlyList<SharedSession>> ListDeletableAsync(DateTimeOffset guestCutoff, DateTimeOffset historyCutoff, int maxCount, CancellationToken cancellationToken = default);

    void Add(SharedSession session);

    void Remove(SharedSession session);

    void AddEvent(SessionEvent sessionEvent);

    /// <exception cref="DuplicateSessionCodeException">A concurrent writer took the same join code.</exception>
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}

/// <summary>Raised by stores when the unique join-code constraint is violated.</summary>
public sealed class DuplicateSessionCodeException : Exception
{
    public DuplicateSessionCodeException()
        : base("The generated session code is already in use.")
    {
    }

    public DuplicateSessionCodeException(string message)
        : base(message)
    {
    }

    public DuplicateSessionCodeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public interface ISnippetRepository
{
    Task AddAsync(SavedSnippet snippet, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SavedSnippet>> ListAsync(string userId, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(string userId, Guid snippetId, CancellationToken cancellationToken = default);

    Task<int> DeleteAllAsync(string userId, CancellationToken cancellationToken = default);
}
