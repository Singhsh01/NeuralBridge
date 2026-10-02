using Microsoft.Extensions.Logging;
using NeuralBridge.Application.Abstractions;
using NeuralBridge.Domain.Sessions;

namespace NeuralBridge.Application.Sessions;

/// <summary>Session data that belongs to a signed-in user: history, clearing it, and erasing everything on account deletion.</summary>
public interface IAccountDataService
{
    /// <summary>Ended sessions the user chose to keep in history (metadata only, newest first).</summary>
    Task<IReadOnlyList<SessionHistoryItem>> ListHistoryAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>Deletes every ended session of the user. Running sessions are not touched.</summary>
    Task<int> ClearHistoryAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>Closes the user's running sessions (disconnecting everyone), then deletes all their sessions and saved text.</summary>
    Task DeleteAllAsync(string userId, CancellationToken cancellationToken = default);
}

public sealed class AccountDataService : IAccountDataService
{
    private readonly ISessionStore _store;
    private readonly ISessionOwnerService _owner;
    private readonly ISnippetRepository _snippets;
    private readonly ISessionLockProvider _locks;
    private readonly TimeProvider _time;
    private readonly ILogger<AccountDataService> _logger;

    public AccountDataService(
        ISessionStore store,
        ISessionOwnerService owner,
        ISnippetRepository snippets,
        ISessionLockProvider locks,
        TimeProvider time,
        ILogger<AccountDataService> logger)
    {
        _store = store;
        _owner = owner;
        _snippets = snippets;
        _locks = locks;
        _time = time;
        _logger = logger;
    }

    public async Task<IReadOnlyList<SessionHistoryItem>> ListHistoryAsync(string userId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        var now = _time.GetUtcNow();
        await using var uow = _store.Begin();
        var sessions = await uow.ListByOwnerAsync(userId, cancellationToken);
        return sessions
            .Where(s => !s.IsActive(now) && s.RetainHistory)
            .OrderByDescending(s => s.EndedAt ?? s.ExpiresAt)
            .Select(s => new SessionHistoryItem(
                s.PublicId,
                s.Code.Formatted,
                s.CreatedAt,
                s.EndedAt ?? s.ExpiresAt,
                s.GetStatus(now),
                s.EndReason ?? SessionEndReason.Expired,
                s.TotalParticipantCount))
            .ToList();
    }

    public async Task<int> ClearHistoryAsync(string userId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        IReadOnlyList<Guid> ended;
        await using (var lookup = _store.Begin())
        {
            ended = (await lookup.ListByOwnerAsync(userId, cancellationToken))
                .Where(s => s.Status != SessionStatus.Active)
                .Select(s => s.Id)
                .ToList();
        }

        var removed = 0;
        foreach (var id in ended)
        {
            await using var sessionLock = await _locks.AcquireAsync(id, cancellationToken);
            await using var uow = _store.Begin();
            var session = await uow.FindByIdAsync(id, cancellationToken);
            if (session is null || session.OwnerUserId != userId || session.Status == SessionStatus.Active)
            {
                continue;
            }

            uow.Remove(session);
            await uow.SaveChangesAsync(cancellationToken);
            removed++;
        }

        _logger.LogInformation("Cleared {Count} history entries for a user", removed);
        return removed;
    }

    public async Task DeleteAllAsync(string userId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        List<string> running;
        await using (var lookup = _store.Begin())
        {
            running = (await lookup.ListActiveByOwnerAsync(userId, cancellationToken)).Select(s => s.PublicId).ToList();
        }

        foreach (var publicId in running)
        {
            // Disconnects participants and purges live text through the normal close path.
            await _owner.CloseAsAccountOwnerAsync(publicId, userId, cancellationToken);
        }

        await ClearHistoryAsync(userId, cancellationToken);
        await _snippets.DeleteAllAsync(userId, cancellationToken);
        _logger.LogInformation("Deleted all session data for a user ({Closed} running sessions closed)", running.Count);
    }
}
