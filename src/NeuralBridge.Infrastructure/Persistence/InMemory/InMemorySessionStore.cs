using System.Reflection;
using NeuralBridge.Application.Abstractions;
using NeuralBridge.Domain.Sessions;

namespace NeuralBridge.Infrastructure.Persistence.InMemory;

/// <summary>
/// Thread-safe in-memory session store. Each unit of work gets <b>copies</b> of the stored
/// aggregates, and changes become visible only on <c>SaveChangesAsync</c>. That mirrors
/// EF Core semantics, so services behave identically on both providers.
/// </summary>
public sealed class InMemorySessionStore : ISessionStore
{
    private readonly Dictionary<Guid, SharedSession> _sessions = [];
    private readonly List<SessionEvent> _events = [];
    private readonly Lock _gate = new();

    public ISessionUnitOfWork Begin() => new UnitOfWork(this);

    /// <summary>Audit events (tests/diagnostics).</summary>
    public IReadOnlyList<SessionEvent> Events
    {
        get
        {
            lock (_gate)
            {
                return _events.ToList();
            }
        }
    }

    public int SessionCount
    {
        get
        {
            lock (_gate)
            {
                return _sessions.Count;
            }
        }
    }

    private SharedSession? Find(Func<SharedSession, bool> predicate)
    {
        lock (_gate)
        {
            var match = _sessions.Values.FirstOrDefault(predicate);
            return match is null ? null : AggregateCloner.Clone(match);
        }
    }

    private List<SharedSession> Query(Func<SharedSession, bool> predicate, int max = int.MaxValue)
    {
        lock (_gate)
        {
            return _sessions.Values.Where(predicate).Take(max).Select(AggregateCloner.Clone).ToList();
        }
    }

    private void Commit(IReadOnlyCollection<SharedSession> upserts, IReadOnlyCollection<Guid> removals, IReadOnlyCollection<SessionEvent> events)
    {
        lock (_gate)
        {
            foreach (var session in upserts)
            {
                var clash = _sessions.Values.Any(s => s.Id != session.Id && s.JoinCode == session.JoinCode);
                if (clash)
                {
                    throw new DuplicateSessionCodeException();
                }
            }

            foreach (var session in upserts)
            {
                _sessions[session.Id] = AggregateCloner.Clone(session);
            }

            foreach (var id in removals)
            {
                _sessions.Remove(id);
                _events.RemoveAll(e => e.SessionId == id);
            }

            _events.AddRange(events.Where(e => _sessions.ContainsKey(e.SessionId)));
        }
    }

    private sealed class UnitOfWork(InMemorySessionStore store) : ISessionUnitOfWork
    {
        private readonly Dictionary<Guid, SharedSession> _tracked = [];
        private readonly HashSet<Guid> _removed = [];
        private readonly List<SessionEvent> _events = [];

        public Task<SharedSession?> FindByIdAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Track(store.Find(s => s.Id == sessionId)));

        public Task<SharedSession?> FindByPublicIdAsync(string publicId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Track(store.Find(s => s.PublicId == publicId)));

        public Task<SharedSession?> FindByCodeAsync(string normalizedCode, CancellationToken cancellationToken = default) =>
            Task.FromResult(Track(store.Find(s => s.JoinCode == normalizedCode)));

        public Task<bool> CodeInUseAsync(string normalizedCode, CancellationToken cancellationToken = default) =>
            Task.FromResult(store.Find(s => s.JoinCode == normalizedCode) is not null
                || _tracked.Values.Any(s => s.JoinCode == normalizedCode));

        public Task<IReadOnlyList<SharedSession>> ListActiveByOwnerAsync(string userId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SharedSession>>(TrackAll(store.Query(s => s.OwnerUserId == userId && s.Status == SessionStatus.Active)));

        public Task<IReadOnlyList<SharedSession>> ListOverdueAsync(DateTimeOffset now, int maxCount, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SharedSession>>(TrackAll(store.Query(s => s.Status == SessionStatus.Active && s.ExpiresAt <= now, maxCount)));

        public Task<IReadOnlyList<SharedSession>> ListByOwnerAsync(string userId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SharedSession>>(TrackAll(store.Query(s => s.OwnerUserId == userId)));

        public Task<IReadOnlyList<SharedSession>> ListDeletableAsync(DateTimeOffset guestCutoff, DateTimeOffset historyCutoff, int maxCount, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SharedSession>>(TrackAll(store.Query(
                s => s.Status != SessionStatus.Active && (s.RetainHistory ? s.EndedAt < historyCutoff : s.EndedAt < guestCutoff),
                maxCount)));

        public void Add(SharedSession session) => _tracked[session.Id] = session;

        public void Remove(SharedSession session)
        {
            _tracked.Remove(session.Id);
            _removed.Add(session.Id);
        }

        public void AddEvent(SessionEvent sessionEvent) => _events.Add(sessionEvent);

        public Task SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            store.Commit(_tracked.Values.ToList(), _removed.ToList(), _events.ToList());
            _events.Clear();
            _removed.Clear();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private SharedSession? Track(SharedSession? session)
        {
            if (session is null)
            {
                return null;
            }

            // Within one unit of work, repeated loads return the same instance (identity map).
            if (_tracked.TryGetValue(session.Id, out var existing))
            {
                return existing;
            }

            _tracked[session.Id] = session;
            return session;
        }

        private List<SharedSession> TrackAll(List<SharedSession> sessions) => sessions.Select(s => Track(s)!).ToList();
    }

    /// <summary>Deep-copies a session aggregate (session + participants) via member-wise clones.</summary>
    internal static class AggregateCloner
    {
        private static readonly MethodInfo MemberwiseCloneMethod =
            typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!;

        private static readonly FieldInfo ParticipantsField =
            typeof(SharedSession).GetField("_participants", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("SharedSession._participants not found.");

        public static SharedSession Clone(SharedSession source)
        {
            var copy = (SharedSession)MemberwiseCloneMethod.Invoke(source, null)!;
            var participants = source.Participants
                .Select(p => (SessionParticipant)MemberwiseCloneMethod.Invoke(p, null)!)
                .ToList();
            ParticipantsField.SetValue(copy, participants);
            return copy;
        }
    }
}
