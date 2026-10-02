using NeuralBridge.Application.Abstractions;

namespace NeuralBridge.Infrastructure.Realtime;

/// <summary>Connection-counting presence. A participant is online while at least one of its connections is open.</summary>
public sealed class InMemoryPresenceTracker : IPresenceTracker
{
    private readonly Dictionary<Guid, Dictionary<Guid, HashSet<string>>> _sessions = [];
    private readonly Lock _gate = new();

    public bool Connect(Guid sessionId, Guid participantId, string connectionId)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(sessionId, out var participants))
            {
                participants = [];
                _sessions[sessionId] = participants;
            }

            if (!participants.TryGetValue(participantId, out var connections))
            {
                connections = new HashSet<string>(StringComparer.Ordinal);
                participants[participantId] = connections;
            }

            var wasOffline = connections.Count == 0;
            connections.Add(connectionId);
            return wasOffline;
        }
    }

    public bool Disconnect(Guid sessionId, Guid participantId, string connectionId)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(sessionId, out var participants) ||
                !participants.TryGetValue(participantId, out var connections) ||
                !connections.Remove(connectionId))
            {
                return false;
            }

            if (connections.Count > 0)
            {
                return false;
            }

            participants.Remove(participantId);
            if (participants.Count == 0)
            {
                _sessions.Remove(sessionId);
            }

            return true;
        }
    }

    public IReadOnlySet<Guid> GetOnlineParticipants(Guid sessionId)
    {
        lock (_gate)
        {
            return _sessions.TryGetValue(sessionId, out var participants)
                ? participants.Keys.ToHashSet()
                : new HashSet<Guid>();
        }
    }

    public void ClearSession(Guid sessionId)
    {
        lock (_gate)
        {
            _sessions.Remove(sessionId);
        }
    }
}
