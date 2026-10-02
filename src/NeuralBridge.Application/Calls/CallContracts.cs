namespace NeuralBridge.Application.Calls;

/// <summary>A call member's published media state.</summary>
public sealed record CallMemberState(Guid ParticipantId, bool AudioEnabled, bool VideoEnabled, DateTimeOffset JoinedAt);

/// <summary>A call member as shown to other participants.</summary>
public sealed record CallRosterEntry(Guid ParticipantId, string DisplayName, bool AudioEnabled, bool VideoEnabled, bool IsYou);

/// <summary>In-process registry of who is in each session's call (single instance; swap for a distributed store to scale out).</summary>
public interface ICallRegistry
{
    /// <summary>Adds or updates a member. Returns false when the call is full and the member is new.</summary>
    bool Upsert(Guid sessionId, CallMemberState member, int maxMembers);

    bool Remove(Guid sessionId, Guid participantId);

    IReadOnlyList<CallMemberState> Members(Guid sessionId);

    bool Contains(Guid sessionId, Guid participantId);

    void ClearSession(Guid sessionId);
}
