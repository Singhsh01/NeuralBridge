namespace NeuralBridge.Domain.Sessions;

public enum SessionEventType
{
    Created = 1,
    ParticipantJoined = 2,
    ParticipantLeft = 3,
    ParticipantRemoved = 4,
    Closed = 5,
    Expired = 6,
    Extended = 7,
    CodeRotated = 8,
    PinChanged = 9,
    PinRemoved = 10,
    GuestEditingChanged = 11,
    ContentCleared = 12,
    PinLockout = 13,
}

/// <summary>
/// An audit entry for a session. It never contains document content, codes, PINs or tokens.
/// </summary>
public sealed class SessionEvent
{
    // EF Core
    private SessionEvent()
    {
    }

    public SessionEvent(Guid sessionId, SessionEventType type, DateTimeOffset occurredAt, Guid? participantId = null)
    {
        Id = Guid.NewGuid();
        SessionId = sessionId;
        Type = type;
        OccurredAt = occurredAt;
        ParticipantId = participantId;
    }

    public Guid Id { get; private set; }

    public Guid SessionId { get; private set; }

    public SessionEventType Type { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    public Guid? ParticipantId { get; private set; }
}
