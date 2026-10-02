using NeuralBridge.Domain.Common;

namespace NeuralBridge.Domain.Sessions;

/// <summary>
/// Aggregate root for a temporary sharing session. All invariants about lifetime,
/// capacity, PIN lockout and participant membership live here. Time is always passed in
/// explicitly, which keeps the aggregate deterministic and testable.
/// </summary>
public sealed class SharedSession
{
    private readonly List<SessionParticipant> _participants = [];

    // EF Core
    private SharedSession()
    {
        PublicId = string.Empty;
        JoinCode = string.Empty;
    }

    private SharedSession(
        Guid id,
        string publicId,
        SessionCode code,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt,
        int maxParticipants,
        string? pinHash,
        string? ownerUserId,
        bool retainHistory)
    {
        Id = id;
        PublicId = publicId;
        JoinCode = code.Value;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
        MaxParticipants = maxParticipants;
        PinHash = pinHash;
        OwnerUserId = ownerUserId;
        RetainHistory = ownerUserId is not null && retainHistory;
        Status = SessionStatus.Active;
        GuestEditingEnabled = true;
    }

    /// <summary>Internal primary key. Never shown to users.</summary>
    public Guid Id { get; private set; }

    /// <summary>Random, URL-safe identifier used in workspace URLs. It grants no access by itself.</summary>
    public string PublicId { get; private set; }

    /// <summary>Normalized join code (see <see cref="SessionCode"/>).</summary>
    public string JoinCode { get; private set; }

    public string? PinHash { get; private set; }

    public bool HasPin => PinHash is not null;

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? EndedAt { get; private set; }

    public SessionStatus Status { get; private set; }

    public SessionEndReason? EndReason { get; private set; }

    /// <summary>Identity user id of the creator when they were signed in.</summary>
    public string? OwnerUserId { get; private set; }

    public bool GuestEditingEnabled { get; private set; }

    /// <summary>
    /// The signed-in owner opted to keep a history entry (metadata only, never text) after the
    /// session ends. Guest sessions never retain history.
    /// </summary>
    public bool RetainHistory { get; private set; }

    public int MaxParticipants { get; private set; }

    public int FailedPinAttempts { get; private set; }

    public DateTimeOffset? PinLockedUntil { get; private set; }

    public IReadOnlyList<SessionParticipant> Participants => _participants;

    public SessionCode Code => SessionCode.FromNormalized(JoinCode);

    public static SharedSession Create(
        Guid id,
        string publicId,
        SessionCode code,
        DateTimeOffset now,
        TimeSpan lifetime,
        int maxParticipants,
        string? pinHash,
        string? ownerUserId,
        bool retainHistory = false)
    {
        if (lifetime <= TimeSpan.Zero)
        {
            throw new DomainException(DomainErrorCodes.InvalidArgument, "Lifetime must be positive.");
        }

        if (maxParticipants < 2)
        {
            throw new DomainException(DomainErrorCodes.InvalidArgument, "A session must allow at least two participants.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(publicId);
        return new SharedSession(id, publicId, code, now, now + lifetime, maxParticipants, pinHash, ownerUserId, retainHistory);
    }

    /// <summary>The effective status at <paramref name="now"/>. Overdue sessions count as expired even before the sweeper runs.</summary>
    public SessionStatus GetStatus(DateTimeOffset now) =>
        Status == SessionStatus.Active && now >= ExpiresAt ? SessionStatus.Expired : Status;

    public bool IsActive(DateTimeOffset now) => GetStatus(now) == SessionStatus.Active;

    public TimeSpan RemainingTime(DateTimeOffset now) =>
        IsActive(now) ? ExpiresAt - now : TimeSpan.Zero;

    public int ActiveParticipantCount => _participants.Count(p => p.IsActive);

    /// <summary>Everyone who ever joined (used for history).</summary>
    public int TotalParticipantCount => _participants.Count;

    public SessionParticipant? FindParticipant(Guid participantId) =>
        _participants.FirstOrDefault(p => p.Id == participantId);

    public bool CanEdit(SessionParticipant participant) =>
        participant.IsActive && (participant.IsOwner || GuestEditingEnabled);

    public SessionParticipant AddParticipant(
        Guid participantId,
        string displayName,
        string tokenHash,
        bool isOwner,
        string? userId,
        DateTimeOffset now)
    {
        EnsureActive(now);

        // Owners (e.g. a signed-in owner reopening from another device) are never locked out by capacity.
        if (!isOwner && ActiveParticipantCount >= MaxParticipants)
        {
            throw new DomainException(DomainErrorCodes.CapacityReached, "The session has reached its participant limit.");
        }

        var participant = new SessionParticipant(participantId, Id, displayName, tokenHash, isOwner, userId, now);
        _participants.Add(participant);
        return participant;
    }

    public void MarkParticipantLeft(Guid participantId, DateTimeOffset now)
    {
        var participant = FindParticipant(participantId)
            ?? throw new DomainException(DomainErrorCodes.ParticipantNotFound, "Participant not found.");
        participant.MarkLeft(now);
    }

    public SessionParticipant RemoveParticipant(Guid participantId, DateTimeOffset now)
    {
        EnsureActive(now);
        var participant = FindParticipant(participantId);
        if (participant is null || !participant.IsActive)
        {
            throw new DomainException(DomainErrorCodes.ParticipantNotFound, "Participant not found.");
        }

        if (participant.IsOwner)
        {
            throw new DomainException(DomainErrorCodes.CannotRemoveOwner, "Owners cannot be removed.");
        }

        participant.MarkRemoved(now);
        return participant;
    }

    public void Close(DateTimeOffset now)
    {
        EnsureActive(now);
        End(SessionStatus.Closed, SessionEndReason.ClosedByOwner, now);
    }

    /// <summary>Marks an overdue session as expired. Returns <c>false</c> if it is not overdue or has already ended.</summary>
    public bool TryExpire(DateTimeOffset now)
    {
        if (Status != SessionStatus.Active || now < ExpiresAt)
        {
            return false;
        }

        End(SessionStatus.Expired, SessionEndReason.Expired, now);
        return true;
    }

    /// <summary>
    /// Extends the expiry by <paramref name="duration"/>. The result is capped so the total
    /// lifetime never exceeds <paramref name="maxTotalLifetime"/> from creation.
    /// </summary>
    public DateTimeOffset Extend(TimeSpan duration, TimeSpan maxTotalLifetime, DateTimeOffset now)
    {
        EnsureActive(now);
        if (duration <= TimeSpan.Zero)
        {
            throw new DomainException(DomainErrorCodes.InvalidArgument, "Extension must be positive.");
        }

        var ceiling = CreatedAt + maxTotalLifetime;
        if (ExpiresAt >= ceiling)
        {
            throw new DomainException(DomainErrorCodes.ExtensionLimitReached, "This session has reached its maximum lifetime.");
        }

        var target = ExpiresAt + duration;
        ExpiresAt = target > ceiling ? ceiling : target;
        return ExpiresAt;
    }

    public void RotateCode(SessionCode newCode, DateTimeOffset now)
    {
        EnsureActive(now);
        JoinCode = newCode.Value;
    }

    public void SetPin(string pinHash, DateTimeOffset now)
    {
        EnsureActive(now);
        ArgumentException.ThrowIfNullOrWhiteSpace(pinHash);
        PinHash = pinHash;
        ResetPinFailures();
    }

    public void RemovePin(DateTimeOffset now)
    {
        EnsureActive(now);
        PinHash = null;
        ResetPinFailures();
    }

    public void SetGuestEditing(bool enabled, DateTimeOffset now)
    {
        EnsureActive(now);
        GuestEditingEnabled = enabled;
    }

    public bool IsPinLocked(DateTimeOffset now) => PinLockedUntil is { } until && now < until;

    /// <summary>Records a wrong PIN. Returns <c>true</c> when this attempt triggered a lockout.</summary>
    public bool RegisterFailedPinAttempt(DateTimeOffset now, int maxAttempts, TimeSpan lockoutDuration)
    {
        if (PinLockedUntil is { } until && now >= until)
        {
            ResetPinFailures();
        }

        FailedPinAttempts++;
        if (FailedPinAttempts >= maxAttempts)
        {
            PinLockedUntil = now + lockoutDuration;
            FailedPinAttempts = 0;
            return true;
        }

        return false;
    }

    public void ResetPinFailures()
    {
        FailedPinAttempts = 0;
        PinLockedUntil = null;
    }

    public void EnsureActive(DateTimeOffset now)
    {
        if (!IsActive(now))
        {
            throw new DomainException(DomainErrorCodes.SessionNotActive, "The session is no longer active.");
        }
    }

    private void End(SessionStatus status, SessionEndReason reason, DateTimeOffset now)
    {
        Status = status;
        EndReason = reason;
        EndedAt = now;
        PinHash = null;
        foreach (var participant in _participants)
        {
            participant.MarkLeft(now);
        }
    }
}
