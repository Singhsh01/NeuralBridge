using NeuralBridge.Domain.Sessions;

namespace NeuralBridge.Application.Realtime;

/// <summary>Base type for everything broadcast to the participants of one session.</summary>
public abstract record SessionNotification(Guid SessionId);

/// <param name="OriginId">The circuit/connection that produced the change. Its own subscriber ignores it to prevent echo loops.</param>
public sealed record DocumentChangedNotification(
    Guid SessionId,
    string Content,
    long Version,
    DateTimeOffset UpdatedAt,
    Guid EditorParticipantId,
    string EditorName,
    string? OriginId) : SessionNotification(SessionId);

public sealed record TypingNotification(
    Guid SessionId,
    Guid ParticipantId,
    string DisplayName,
    string? OriginId) : SessionNotification(SessionId);

public sealed record ParticipantsChangedNotification(Guid SessionId) : SessionNotification(SessionId);

/// <summary>Expiry, PIN, code or guest-editing changed.</summary>
public sealed record SessionSettingsChangedNotification(Guid SessionId) : SessionNotification(SessionId);

public sealed record ParticipantRemovedNotification(Guid SessionId, Guid ParticipantId) : SessionNotification(SessionId);

public sealed record SessionEndedNotification(Guid SessionId, SessionEndReason Reason) : SessionNotification(SessionId);

/// <summary>
/// Process-local publish/subscribe for session notifications. Implementations must isolate
/// subscriber failures. One slow or faulty circuit must never block the others.
/// </summary>
public interface ISessionEventBus
{
    Task PublishAsync(SessionNotification notification);

    /// <summary>Receives notifications for one session only.</summary>
    IDisposable Subscribe(Guid sessionId, Func<SessionNotification, Task> handler);

    /// <summary>Receives notifications for all sessions (used by the SignalR hub relay).</summary>
    IDisposable SubscribeAll(Func<SessionNotification, Task> handler);
}

/// <summary>Someone joined or left the call, or changed their microphone/camera state.</summary>
public sealed record CallStateChangedNotification(Guid SessionId) : SessionNotification(SessionId);

/// <summary>
/// One WebRTC signaling message addressed to a single participant. The payload is opaque to
/// the server (SDP or ICE candidate JSON) and is never logged or forwarded to hub groups.
/// </summary>
public sealed record CallSignalNotification(Guid SessionId, Guid FromParticipantId, Guid ToParticipantId, string Payload) : SessionNotification(SessionId);
