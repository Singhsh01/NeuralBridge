using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NeuralBridge.Application.Abstractions;
using NeuralBridge.Application.Options;
using NeuralBridge.Application.Realtime;
using NeuralBridge.Domain.Sessions;

namespace NeuralBridge.Application.Sessions;

/// <param name="Expired">Sessions transitioned to expired in this sweep.</param>
/// <param name="Deleted">Ended sessions whose metadata was deleted after the retention period.</param>
/// <param name="OrphanDocumentsRemoved">Live documents whose session was no longer active.</param>
public sealed record CleanupReport(int Expired, int Deleted, int OrphanDocumentsRemoved);

public interface ISessionCleanupService
{
    Task<CleanupReport> SweepAsync(CancellationToken cancellationToken = default);

    /// <summary>Expires one session if it is due. Workspaces call this when their countdown reaches zero.</summary>
    Task<bool> ExpireIfDueAsync(Guid sessionId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Cleanup is safe in two ways. Expiry is enforced lazily on every access through
/// <see cref="SharedSession.GetStatus"/>, so correctness never depends on this sweeper
/// running on time. The sweeper takes the same per-session lock as every other mutation,
/// so it can't race an owner action.
/// </summary>
public sealed class SessionCleanupService : ISessionCleanupService
{
    private const int BatchSize = 200;

    private readonly ISessionStore _store;
    private readonly ISessionLockProvider _locks;
    private readonly IDocumentStore _documents;
    private readonly IPresenceTracker _presence;
    private readonly ISessionEventBus _bus;
    private readonly TimeProvider _time;
    private readonly SessionOptions _options;
    private readonly ILogger<SessionCleanupService> _logger;

    public SessionCleanupService(
        ISessionStore store,
        ISessionLockProvider locks,
        IDocumentStore documents,
        IPresenceTracker presence,
        ISessionEventBus bus,
        TimeProvider time,
        IOptions<SessionOptions> options,
        ILogger<SessionCleanupService> logger)
    {
        _store = store;
        _locks = locks;
        _documents = documents;
        _presence = presence;
        _bus = bus;
        _time = time;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<CleanupReport> SweepAsync(CancellationToken cancellationToken = default)
    {
        var now = _time.GetUtcNow();

        IReadOnlyList<Guid> overdue;
        await using (var uow = _store.Begin())
        {
            overdue = (await uow.ListOverdueAsync(now, BatchSize, cancellationToken)).Select(s => s.Id).ToList();
        }

        var expired = 0;
        foreach (var id in overdue)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await ExpireIfDueAsync(id, cancellationToken))
            {
                expired++;
            }
        }

        var deleted = await DeleteEndedAsync(now, cancellationToken);
        var orphans = await RemoveOrphanDocumentsAsync(cancellationToken);

        if (expired + deleted + orphans > 0)
        {
            _logger.LogInformation(
                "Cleanup sweep: {Expired} expired, {Deleted} deleted, {Orphans} orphan documents removed",
                expired,
                deleted,
                orphans);
        }

        return new CleanupReport(expired, deleted, orphans);
    }

    public async Task<bool> ExpireIfDueAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        await using var sessionLock = await _locks.AcquireAsync(sessionId, cancellationToken);
        await using var uow = _store.Begin();
        var session = await uow.FindByIdAsync(sessionId, cancellationToken);
        var now = _time.GetUtcNow();
        if (session is null || !session.TryExpire(now))
        {
            return false;
        }

        uow.AddEvent(new SessionEvent(session.Id, SessionEventType.Expired, now));
        await uow.SaveChangesAsync(cancellationToken);

        _documents.Remove(session.Id);
        _presence.ClearSession(session.Id);
        await _bus.PublishAsync(new SessionEndedNotification(session.Id, SessionEndReason.Expired));
        _logger.LogInformation("Session {SessionId} expired", session.Id);
        return true;
    }

    private async Task<int> DeleteEndedAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var uow = _store.Begin();
        var ended = await uow.ListDeletableAsync(
            now - _options.EndedSessionRetention,
            now - _options.AccountHistoryRetention,
            BatchSize,
            cancellationToken);
        foreach (var session in ended)
        {
            _documents.Remove(session.Id);
            uow.Remove(session);
        }

        if (ended.Count > 0)
        {
            await uow.SaveChangesAsync(cancellationToken);
        }

        return ended.Count;
    }

    private async Task<int> RemoveOrphanDocumentsAsync(CancellationToken cancellationToken)
    {
        var removed = 0;
        await using var uow = _store.Begin();
        foreach (var id in _documents.SessionIds)
        {
            var session = await uow.FindByIdAsync(id, cancellationToken);
            // Overdue-but-still-active sessions were handled by ExpireIfDueAsync above.
            if (session is null || session.Status != SessionStatus.Active)
            {
                if (_documents.Remove(id))
                {
                    removed++;
                }
            }
        }

        return removed;
    }
}
