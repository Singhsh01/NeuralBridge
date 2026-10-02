using NeuralBridge.Application.Calls;

namespace NeuralBridge.Infrastructure.Realtime;

public sealed class InMemoryCallRegistry : ICallRegistry
{
    private readonly Dictionary<Guid, Dictionary<Guid, CallMemberState>> _calls = [];
    private readonly Lock _gate = new();

    public bool Upsert(Guid sessionId, CallMemberState member, int maxMembers)
    {
        lock (_gate)
        {
            if (!_calls.TryGetValue(sessionId, out var members))
            {
                members = [];
                _calls[sessionId] = members;
            }

            if (!members.ContainsKey(member.ParticipantId) && members.Count >= maxMembers)
            {
                return false;
            }

            members[member.ParticipantId] = member;
            return true;
        }
    }

    public bool Remove(Guid sessionId, Guid participantId)
    {
        lock (_gate)
        {
            if (!_calls.TryGetValue(sessionId, out var members) || !members.Remove(participantId))
            {
                return false;
            }

            if (members.Count == 0)
            {
                _calls.Remove(sessionId);
            }

            return true;
        }
    }

    public IReadOnlyList<CallMemberState> Members(Guid sessionId)
    {
        lock (_gate)
        {
            return _calls.TryGetValue(sessionId, out var members) ? members.Values.ToList() : [];
        }
    }

    public bool Contains(Guid sessionId, Guid participantId)
    {
        lock (_gate)
        {
            return _calls.TryGetValue(sessionId, out var members) && members.ContainsKey(participantId);
        }
    }

    public void ClearSession(Guid sessionId)
    {
        lock (_gate)
        {
            _calls.Remove(sessionId);
        }
    }
}
