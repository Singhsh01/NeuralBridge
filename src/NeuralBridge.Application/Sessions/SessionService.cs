using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NeuralBridge.Application.Abstractions;
using NeuralBridge.Application.Common;
using NeuralBridge.Application.Options;
using NeuralBridge.Application.Realtime;
using NeuralBridge.Domain.Common;
using NeuralBridge.Domain.Sessions;

namespace NeuralBridge.Application.Sessions;

public interface ISessionService
{
    Task<Result<CreatedSession>> CreateAsync(CreateSessionCommand command, CancellationToken cancellationToken = default);

    Task<Result<JoinedSession>> JoinAsync(JoinSessionCommand command, CancellationToken cancellationToken = default);

    /// <summary>Lets a signed-in owner open their account session from any device (issues a new owner participant).</summary>
    Task<Result<ParticipantCredentials>> OpenAsAccountOwnerAsync(string publicId, string userId, string? displayName, CancellationToken cancellationToken = default);

    Task<Result<SessionSnapshot>> GetSnapshotAsync(ParticipantCredentials credentials, CancellationToken cancellationToken = default);

    Task<Result> LeaveAsync(ParticipantCredentials credentials, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OwnedSessionSummary>> ListOwnedAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>Returns the account owner's session status without revealing anything to non-owners.</summary>
    Task<bool> IsAccountOwnerAsync(string publicId, string userId, CancellationToken cancellationToken = default);
}

public sealed class SessionService : ISessionService
{
    private readonly ISessionStore _store;
    private readonly ISessionCodeGenerator _codes;
    private readonly SessionCodeAllocator _codeAllocator;
    private readonly ISecretHasher _hasher;
    private readonly ITokenService _tokens;
    private readonly IAppRateLimiter _rateLimiter;
    private readonly ISessionLockProvider _locks;
    private readonly IDocumentStore _documents;
    private readonly IPresenceTracker _presence;
    private readonly ISessionEventBus _bus;
    private readonly ISessionAuthorizationService _authorization;
    private readonly TimeProvider _time;
    private readonly SessionOptions _options;
    private readonly ILogger<SessionService> _logger;

    public SessionService(
        ISessionStore store,
        ISessionCodeGenerator codes,
        SessionCodeAllocator codeAllocator,
        ISecretHasher hasher,
        ITokenService tokens,
        IAppRateLimiter rateLimiter,
        ISessionLockProvider locks,
        IDocumentStore documents,
        IPresenceTracker presence,
        ISessionEventBus bus,
        ISessionAuthorizationService authorization,
        TimeProvider time,
        IOptions<SessionOptions> options,
        ILogger<SessionService> logger)
    {
        _store = store;
        _codes = codes;
        _codeAllocator = codeAllocator;
        _hasher = hasher;
        _tokens = tokens;
        _rateLimiter = rateLimiter;
        _locks = locks;
        _documents = documents;
        _presence = presence;
        _bus = bus;
        _authorization = authorization;
        _time = time;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<Result<CreatedSession>> CreateAsync(CreateSessionCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!_rateLimiter.TryAcquire(RateLimitPolicy.CreateSession, command.ClientKey))
        {
            _logger.LogWarning("Session creation rate-limited");
            return Result<CreatedSession>.Failure(SessionError.RateLimited);
        }

        var lifetime = command.LifetimeMinutes is { } minutes ? TimeSpan.FromMinutes(minutes) : _options.DefaultLifetime;
        if (!_options.AllowedLifetimes.Contains(lifetime))
        {
            return Result<CreatedSession>.Failure(SessionError.ValidationFailed, "Please choose one of the offered session durations.");
        }

        var pin = PinPolicy.Normalize(command.Pin);
        var pinValidation = PinPolicy.Validate(pin, _options);
        if (pinValidation is not null)
        {
            return Result<CreatedSession>.Failure(SessionError.ValidationFailed, pinValidation);
        }

        var pinHash = pin is null ? null : _hasher.Hash(pin);
        var displayName = SessionParticipant.NormalizeDisplayName(command.DisplayName, "Host");
        var token = _tokens.GenerateToken();
        var participantId = Guid.NewGuid();

        for (var attempt = 1; ; attempt++)
        {
            var now = _time.GetUtcNow();
            await using var uow = _store.Begin();
            var code = await _codeAllocator.AllocateAsync(uow, cancellationToken);
            var session = SharedSession.Create(
                Guid.NewGuid(),
                _codes.GeneratePublicId(),
                code,
                now,
                lifetime,
                _options.MaxParticipants,
                pinHash,
                command.OwnerUserId,
                command.RetainHistory && _options.AccountHistoryRetentionDays > 0);
            session.AddParticipant(participantId, displayName, _tokens.HashToken(token), isOwner: true, command.OwnerUserId, now);
            uow.Add(session);
            uow.AddEvent(new SessionEvent(session.Id, SessionEventType.Created, now, participantId));

            try
            {
                await uow.SaveChangesAsync(cancellationToken);
            }
            catch (DuplicateSessionCodeException) when (attempt < _options.CodeGenerationMaxAttempts)
            {
                _logger.LogInformation("Join code collision on save; retrying (attempt {Attempt})", attempt);
                continue;
            }

            _documents.GetOrCreate(session.Id, now);
            _logger.LogInformation(
                "Session {SessionId} created (lifetime {LifetimeMinutes} min, pin {HasPin}, account {IsAccount})",
                session.Id,
                (int)lifetime.TotalMinutes,
                pinHash is not null,
                command.OwnerUserId is not null);

            return Result<CreatedSession>.Success(new CreatedSession(
                session.PublicId,
                code.Formatted,
                session.ExpiresAt,
                new ParticipantCredentials(session.PublicId, participantId, token)));
        }
    }

    public async Task<Result<JoinedSession>> JoinAsync(JoinSessionCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!_rateLimiter.TryAcquire(RateLimitPolicy.JoinSession, command.ClientKey))
        {
            _logger.LogWarning("Join attempt rate-limited");
            return Result<JoinedSession>.Failure(SessionError.RateLimited);
        }

        if (!SessionCode.TryParse(command.Code, out var code))
        {
            return Result<JoinedSession>.Failure(SessionError.InvalidCode);
        }

        Guid sessionId;
        await using (var lookup = _store.Begin())
        {
            var found = await lookup.FindByCodeAsync(code.Value.Value, cancellationToken);
            if (found is null)
            {
                return Result<JoinedSession>.Failure(SessionError.NotFoundOrPinIncorrect);
            }

            sessionId = found.Id;
        }

        await using var sessionLock = await _locks.AcquireAsync(sessionId, cancellationToken);
        await using var uow = _store.Begin();
        var session = await uow.FindByIdAsync(sessionId, cancellationToken);
        if (session is null || session.JoinCode != code.Value.Value)
        {
            // Deleted or code rotated between lookup and lock.
            return Result<JoinedSession>.Failure(SessionError.NotFoundOrPinIncorrect);
        }

        var now = _time.GetUtcNow();
        var status = session.GetStatus(now);

        if (status == SessionStatus.Active && session.HasPin)
        {
            if (session.IsPinLocked(now))
            {
                return Result<JoinedSession>.Failure(SessionError.PinLocked);
            }

            var pin = PinPolicy.Normalize(command.Pin);
            if (pin is null || !_hasher.Verify(pin, session.PinHash!))
            {
                var lockedNow = session.RegisterFailedPinAttempt(now, _options.MaxPinAttempts, _options.PinLockout);
                if (lockedNow)
                {
                    uow.AddEvent(new SessionEvent(session.Id, SessionEventType.PinLockout, now));
                    _logger.LogWarning("Session {SessionId} PIN entry locked after repeated failures", session.Id);
                }

                await uow.SaveChangesAsync(cancellationToken);

                // Same answer as an unknown code, so a protected session's existence is not revealed.
                return Result<JoinedSession>.Failure(SessionError.NotFoundOrPinIncorrect);
            }

            session.ResetPinFailures();
        }

        if (status == SessionStatus.Expired)
        {
            return Result<JoinedSession>.Failure(SessionError.Expired);
        }

        if (status == SessionStatus.Closed)
        {
            return Result<JoinedSession>.Failure(SessionError.Closed);
        }

        var displayName = SessionParticipant.NormalizeDisplayName(command.DisplayName, $"Guest {session.Participants.Count}");
        var token = _tokens.GenerateToken();
        SessionParticipant participant;
        try
        {
            participant = session.AddParticipant(Guid.NewGuid(), displayName, _tokens.HashToken(token), isOwner: false, userId: null, now);
        }
        catch (DomainException ex) when (ex.Code == DomainErrorCodes.CapacityReached)
        {
            return Result<JoinedSession>.Failure(SessionError.CapacityReached);
        }

        uow.AddEvent(new SessionEvent(session.Id, SessionEventType.ParticipantJoined, now, participant.Id));
        await uow.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Participant {ParticipantId} joined session {SessionId}", participant.Id, session.Id);

        await _bus.PublishAsync(new ParticipantsChangedNotification(session.Id));
        return Result<JoinedSession>.Success(new JoinedSession(
            session.PublicId,
            new ParticipantCredentials(session.PublicId, participant.Id, token)));
    }

    public async Task<Result<ParticipantCredentials>> OpenAsAccountOwnerAsync(string publicId, string userId, string? displayName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        Guid sessionId;
        await using (var lookup = _store.Begin())
        {
            var found = await lookup.FindByPublicIdAsync(publicId, cancellationToken);
            if (found is null || found.OwnerUserId != userId)
            {
                return Result<ParticipantCredentials>.Failure(SessionError.NotFound);
            }

            sessionId = found.Id;
        }

        await using var sessionLock = await _locks.AcquireAsync(sessionId, cancellationToken);
        await using var uow = _store.Begin();
        var session = await uow.FindByIdAsync(sessionId, cancellationToken);
        var now = _time.GetUtcNow();
        if (session is null || session.OwnerUserId != userId)
        {
            return Result<ParticipantCredentials>.Failure(SessionError.NotFound);
        }

        switch (session.GetStatus(now))
        {
            case SessionStatus.Expired:
                return Result<ParticipantCredentials>.Failure(SessionError.Expired);
            case SessionStatus.Closed:
                return Result<ParticipantCredentials>.Failure(SessionError.Closed);
        }

        var token = _tokens.GenerateToken();
        var participant = session.AddParticipant(
            Guid.NewGuid(),
            SessionParticipant.NormalizeDisplayName(displayName, "Host"),
            _tokens.HashToken(token),
            isOwner: true,
            userId,
            now);
        uow.AddEvent(new SessionEvent(session.Id, SessionEventType.ParticipantJoined, now, participant.Id));
        await uow.SaveChangesAsync(cancellationToken);
        await _bus.PublishAsync(new ParticipantsChangedNotification(session.Id));
        return Result<ParticipantCredentials>.Success(new ParticipantCredentials(session.PublicId, participant.Id, token));
    }

    public async Task<Result<SessionSnapshot>> GetSnapshotAsync(ParticipantCredentials credentials, CancellationToken cancellationToken = default)
    {
        await using var uow = _store.Begin();
        var session = await uow.FindByPublicIdAsync(credentials.PublicId, cancellationToken);
        var auth = _authorization.Authorize(session, credentials, requireOwner: false);
        if (!auth.Succeeded)
        {
            return Result<SessionSnapshot>.Failure(auth.Error);
        }

        return Result<SessionSnapshot>.Success(BuildSnapshot(session!, auth.Value!));
    }

    public async Task<Result> LeaveAsync(ParticipantCredentials credentials, CancellationToken cancellationToken = default)
    {
        var auth = await _authorization.AuthorizeAsync(credentials, cancellationToken);
        if (!auth.Succeeded)
        {
            return Result.Failure(auth.Error);
        }

        var context = auth.Value!;
        await using var sessionLock = await _locks.AcquireAsync(context.SessionId, cancellationToken);
        await using var uow = _store.Begin();
        var session = await uow.FindByIdAsync(context.SessionId, cancellationToken);
        if (session is null)
        {
            return Result.Failure(SessionError.NotFound);
        }

        var now = _time.GetUtcNow();
        session.MarkParticipantLeft(context.ParticipantId, now);
        uow.AddEvent(new SessionEvent(session.Id, SessionEventType.ParticipantLeft, now, context.ParticipantId));
        await uow.SaveChangesAsync(cancellationToken);
        await _bus.PublishAsync(new ParticipantsChangedNotification(session.Id));
        return Result.Success();
    }

    public async Task<IReadOnlyList<OwnedSessionSummary>> ListOwnedAsync(string userId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        var now = _time.GetUtcNow();
        await using var uow = _store.Begin();
        var sessions = await uow.ListActiveByOwnerAsync(userId, cancellationToken);
        return sessions
            .Where(s => s.IsActive(now))
            .OrderByDescending(s => s.CreatedAt)
            .Select(s =>
            {
                var online = _presence.GetOnlineParticipants(s.Id);
                return new OwnedSessionSummary(
                    s.PublicId,
                    s.Code.Formatted,
                    s.CreatedAt,
                    s.ExpiresAt,
                    s.HasPin,
                    s.ActiveParticipantCount,
                    s.Participants.Count(p => p.IsActive && online.Contains(p.Id)));
            })
            .ToList();
    }

    public async Task<bool> IsAccountOwnerAsync(string publicId, string userId, CancellationToken cancellationToken = default)
    {
        await using var uow = _store.Begin();
        var session = await uow.FindByPublicIdAsync(publicId, cancellationToken);
        return session is not null && session.OwnerUserId == userId && session.IsActive(_time.GetUtcNow());
    }

    internal SessionSnapshot BuildSnapshot(SharedSession session, SessionParticipant me)
    {
        var online = _presence.GetOnlineParticipants(session.Id);
        var participants = session.Participants
            .Where(p => p.IsActive)
            .OrderByDescending(p => p.IsOwner)
            .ThenBy(p => p.JoinedAt)
            .Select(p => new ParticipantView(p.Id, p.DisplayName, p.IsOwner, online.Contains(p.Id), p.Id == me.Id, p.JoinedAt))
            .ToList();

        return new SessionSnapshot(
            session.PublicId,
            session.Code.Formatted,
            session.GetStatus(_time.GetUtcNow()),
            session.CreatedAt,
            session.ExpiresAt,
            session.CreatedAt + _options.MaxTotalLifetime,
            session.HasPin,
            session.GuestEditingEnabled,
            session.MaxParticipants,
            me.IsOwner,
            session.CanEdit(me),
            me.Id,
            me.DisplayName,
            participants);
    }
}
