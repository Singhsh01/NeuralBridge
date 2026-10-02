using NeuralBridge.Domain.Sessions;

namespace NeuralBridge.Application.Sessions;

/// <summary>
/// What a browser tab holds to prove it is a participant. Stored client-side encrypted
/// (ASP.NET Data Protection) and re-validated on every operation.
/// </summary>
public sealed record ParticipantCredentials(string PublicId, Guid ParticipantId, string Token);

/// <param name="ClientKey">Rate-limit partition (e.g. client IP). Never persisted or logged.</param>
/// <param name="RetainHistory">Signed-in owner's preference: keep a metadata-only history entry after the session ends.</param>
public sealed record CreateSessionCommand(
    string? DisplayName,
    string? Pin,
    int? LifetimeMinutes,
    string? OwnerUserId,
    string ClientKey,
    bool RetainHistory = false);

public sealed record CreatedSession(
    string PublicId,
    string FormattedCode,
    DateTimeOffset ExpiresAt,
    ParticipantCredentials Credentials);

public sealed record JoinSessionCommand(
    string? Code,
    string? Pin,
    string? DisplayName,
    string ClientKey);

public sealed record JoinedSession(string PublicId, ParticipantCredentials Credentials);

/// <summary>An authorized participant, resolved server-side from credentials for a single operation.</summary>
public sealed record ParticipantContext(
    Guid SessionId,
    string PublicId,
    Guid ParticipantId,
    string DisplayName,
    bool IsOwner,
    bool CanEdit);

public sealed record ParticipantView(
    Guid Id,
    string DisplayName,
    bool IsOwner,
    bool IsOnline,
    bool IsYou,
    DateTimeOffset JoinedAt);

public sealed record SessionSnapshot(
    string PublicId,
    string FormattedCode,
    SessionStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset MaxExpiresAt,
    bool HasPin,
    bool GuestEditingEnabled,
    int MaxParticipants,
    bool IsOwner,
    bool CanEdit,
    Guid MyParticipantId,
    string MyDisplayName,
    IReadOnlyList<ParticipantView> Participants)
{
    public int OnlineCount => Participants.Count(p => p.IsOnline);

    public bool CanExtend => ExpiresAt < MaxExpiresAt;
}

public sealed record OwnedSessionSummary(
    string PublicId,
    string FormattedCode,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    bool HasPin,
    int ActiveParticipants,
    int OnlineParticipants);

/// <summary>An ended (or still running) session in a signed-in user's history. Never includes text.</summary>
public sealed record SessionHistoryItem(
    string PublicId,
    string FormattedCode,
    DateTimeOffset CreatedAt,
    DateTimeOffset? EndedAt,
    SessionStatus Status,
    SessionEndReason? EndReason,
    int Participants)
{
    public TimeSpan Duration(DateTimeOffset now) => (EndedAt ?? now) - CreatedAt;
}
