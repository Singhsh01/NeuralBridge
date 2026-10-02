using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NeuralBridge.Application.Abstractions;
using NeuralBridge.Application.Common;
using NeuralBridge.Application.Options;
using NeuralBridge.Application.Realtime;
using NeuralBridge.Domain.Common;
using NeuralBridge.Domain.Sessions;

namespace NeuralBridge.Application.Sessions;

/// <summary>
/// Owner-only operations. Each one re-authorizes the caller as an owner inside the same
/// per-session lock and unit of work that performs the mutation.
/// </summary>
public interface ISessionOwnerService
{
    Task<Result> CloseAsync(ParticipantCredentials credentials, CancellationToken cancellationToken = default);

    Task<Result<DateTimeOffset>> ExtendAsync(ParticipantCredentials credentials, int minutes, CancellationToken cancellationToken = default);

    Task<Result> RemoveParticipantAsync(ParticipantCredentials credentials, Guid participantId, CancellationToken cancellationToken = default);

    Task<Result> SetGuestEditingAsync(ParticipantCredentials credentials, bool enabled, CancellationToken cancellationToken = default);

    /// <summary>Issues a new join code. The old one stops working; connected participants stay connected.</summary>
    Task<Result<string>> RotateCodeAsync(ParticipantCredentials credentials, CancellationToken cancellationToken = default);

    Task<Result> SetPinAsync(ParticipantCredentials credentials, string? pin, CancellationToken cancellationToken = default);

    Task<Result> RemovePinAsync(ParticipantCredentials credentials, CancellationToken cancellationToken = default);

    /// <summary>Closes an account session from "My sessions" (authorized by user id instead of a participant token).</summary>
    Task<Result> CloseAsAccountOwnerAsync(string publicId, string userId, CancellationToken cancellationToken = default);
}

public sealed class SessionOwnerService : ISessionOwnerService
{
    private readonly ISessionStore _store;
    private readonly ISessionAuthorizationService _authorization;
    private readonly ISessionLockProvider _locks;
    private readonly SessionCodeAllocator _codeAllocator;
    private readonly ISecretHasher _hasher;
    private readonly IDocumentStore _documents;
    private readonly IPresenceTracker _presence;
    private readonly ISessionEventBus _bus;
    private readonly TimeProvider _time;
    private readonly SessionOptions _options;
    private readonly ILogger<SessionOwnerService> _logger;

    public SessionOwnerService(
        ISessionStore store,
        ISessionAuthorizationService authorization,
        ISessionLockProvider locks,
        SessionCodeAllocator codeAllocator,
        ISecretHasher hasher,
        IDocumentStore documents,
        IPresenceTracker presence,
        ISessionEventBus bus,
        TimeProvider time,
        IOptions<SessionOptions> options,
        ILogger<SessionOwnerService> logger)
    {
        _store = store;
        _authorization = authorization;
        _locks = locks;
        _codeAllocator = codeAllocator;
        _hasher = hasher;
        _documents = documents;
        _presence = presence;
        _bus = bus;
        _time = time;
        _options = options.Value;
        _logger = logger;
    }

    public Task<Result> CloseAsync(ParticipantCredentials credentials, CancellationToken cancellationToken = default) =>
        ExecuteAsync(credentials, async (uow, session, owner, now) =>
        {
            session.Close(now);
            uow.AddEvent(new SessionEvent(session.Id, SessionEventType.Closed, now, owner.Id));
            await uow.SaveChangesAsync(cancellationToken);
            await EndAsync(session.Id);
            _logger.LogInformation("Session {SessionId} closed by owner", session.Id);
            return Result.Success();
        }, cancellationToken);

    public async Task<Result<DateTimeOffset>> ExtendAsync(ParticipantCredentials credentials, int minutes, CancellationToken cancellationToken = default)
    {
        if (!_options.ExtensionSteps.Contains(TimeSpan.FromMinutes(minutes)))
        {
            return Result<DateTimeOffset>.Failure(SessionError.ValidationFailed, "Please choose one of the offered extensions.");
        }

        DateTimeOffset newExpiry = default;
        var result = await ExecuteAsync(credentials, async (uow, session, owner, now) =>
        {
            try
            {
                newExpiry = session.Extend(TimeSpan.FromMinutes(minutes), _options.MaxTotalLifetime, now);
            }
            catch (DomainException ex) when (ex.Code == DomainErrorCodes.ExtensionLimitReached)
            {
                return Result.Failure(SessionError.ExtensionLimitReached);
            }

            uow.AddEvent(new SessionEvent(session.Id, SessionEventType.Extended, now, owner.Id));
            await uow.SaveChangesAsync(cancellationToken);
            await _bus.PublishAsync(new SessionSettingsChangedNotification(session.Id));
            return Result.Success();
        }, cancellationToken);

        return result.Succeeded
            ? Result<DateTimeOffset>.Success(newExpiry)
            : Result<DateTimeOffset>.Failure(result.Error, result.Message);
    }

    public Task<Result> RemoveParticipantAsync(ParticipantCredentials credentials, Guid participantId, CancellationToken cancellationToken = default) =>
        ExecuteAsync(credentials, async (uow, session, owner, now) =>
        {
            try
            {
                session.RemoveParticipant(participantId, now);
            }
            catch (DomainException ex) when (ex.Code == DomainErrorCodes.ParticipantNotFound)
            {
                return Result.Failure(SessionError.NotFound, "That participant is no longer in the session.");
            }
            catch (DomainException ex) when (ex.Code == DomainErrorCodes.CannotRemoveOwner)
            {
                return Result.Failure(SessionError.Forbidden, "Owners can't be removed.");
            }

            uow.AddEvent(new SessionEvent(session.Id, SessionEventType.ParticipantRemoved, now, participantId));
            await uow.SaveChangesAsync(cancellationToken);
            await _bus.PublishAsync(new ParticipantRemovedNotification(session.Id, participantId));
            await _bus.PublishAsync(new ParticipantsChangedNotification(session.Id));
            return Result.Success();
        }, cancellationToken);

    public Task<Result> SetGuestEditingAsync(ParticipantCredentials credentials, bool enabled, CancellationToken cancellationToken = default) =>
        ExecuteAsync(credentials, async (uow, session, owner, now) =>
        {
            session.SetGuestEditing(enabled, now);
            uow.AddEvent(new SessionEvent(session.Id, SessionEventType.GuestEditingChanged, now, owner.Id));
            await uow.SaveChangesAsync(cancellationToken);
            await _bus.PublishAsync(new SessionSettingsChangedNotification(session.Id));
            return Result.Success();
        }, cancellationToken);

    public async Task<Result<string>> RotateCodeAsync(ParticipantCredentials credentials, CancellationToken cancellationToken = default)
    {
        var formatted = string.Empty;
        var result = await ExecuteAsync(credentials, async (uow, session, owner, now) =>
        {
            for (var attempt = 1; ; attempt++)
            {
                var code = await _codeAllocator.AllocateAsync(uow, cancellationToken);
                session.RotateCode(code, now);
                try
                {
                    uow.AddEvent(new SessionEvent(session.Id, SessionEventType.CodeRotated, now, owner.Id));
                    await uow.SaveChangesAsync(cancellationToken);
                    formatted = code.Formatted;
                    break;
                }
                catch (DuplicateSessionCodeException) when (attempt < _options.CodeGenerationMaxAttempts)
                {
                    _logger.LogInformation("Join code collision while rotating; retrying");
                }
            }

            await _bus.PublishAsync(new SessionSettingsChangedNotification(session.Id));
            return Result.Success();
        }, cancellationToken);

        return result.Succeeded ? Result<string>.Success(formatted) : Result<string>.Failure(result.Error, result.Message);
    }

    public async Task<Result> SetPinAsync(ParticipantCredentials credentials, string? pin, CancellationToken cancellationToken = default)
    {
        var normalized = PinPolicy.Normalize(pin);
        if (normalized is null)
        {
            return Result.Failure(SessionError.ValidationFailed, "Enter a PIN, or remove the PIN instead.");
        }

        var validation = PinPolicy.Validate(normalized, _options);
        if (validation is not null)
        {
            return Result.Failure(SessionError.ValidationFailed, validation);
        }

        // Hash outside the lock: PBKDF2 is intentionally slow.
        var hash = _hasher.Hash(normalized);
        return await ExecuteAsync(credentials, async (uow, session, owner, now) =>
        {
            session.SetPin(hash, now);
            uow.AddEvent(new SessionEvent(session.Id, SessionEventType.PinChanged, now, owner.Id));
            await uow.SaveChangesAsync(cancellationToken);
            await _bus.PublishAsync(new SessionSettingsChangedNotification(session.Id));
            return Result.Success();
        }, cancellationToken);
    }

    public Task<Result> RemovePinAsync(ParticipantCredentials credentials, CancellationToken cancellationToken = default) =>
        ExecuteAsync(credentials, async (uow, session, owner, now) =>
        {
            session.RemovePin(now);
            uow.AddEvent(new SessionEvent(session.Id, SessionEventType.PinRemoved, now, owner.Id));
            await uow.SaveChangesAsync(cancellationToken);
            await _bus.PublishAsync(new SessionSettingsChangedNotification(session.Id));
            return Result.Success();
        }, cancellationToken);

    public async Task<Result> CloseAsAccountOwnerAsync(string publicId, string userId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        Guid sessionId;
        await using (var lookup = _store.Begin())
        {
            var found = await lookup.FindByPublicIdAsync(publicId, cancellationToken);
            if (found is null || found.OwnerUserId != userId)
            {
                return Result.Failure(SessionError.NotFound);
            }

            sessionId = found.Id;
        }

        await using var sessionLock = await _locks.AcquireAsync(sessionId, cancellationToken);
        await using var uow = _store.Begin();
        var session = await uow.FindByIdAsync(sessionId, cancellationToken);
        var now = _time.GetUtcNow();
        if (session is null || session.OwnerUserId != userId)
        {
            return Result.Failure(SessionError.NotFound);
        }

        if (!session.IsActive(now))
        {
            return Result.Failure(session.GetStatus(now) == SessionStatus.Closed ? SessionError.Closed : SessionError.Expired);
        }

        session.Close(now);
        uow.AddEvent(new SessionEvent(session.Id, SessionEventType.Closed, now));
        await uow.SaveChangesAsync(cancellationToken);
        await EndAsync(session.Id);
        _logger.LogInformation("Session {SessionId} closed by account owner", session.Id);
        return Result.Success();
    }

    private async Task EndAsync(Guid sessionId)
    {
        _documents.Remove(sessionId);
        _presence.ClearSession(sessionId);
        await _bus.PublishAsync(new SessionEndedNotification(sessionId, SessionEndReason.ClosedByOwner));
    }

    private async Task<Result> ExecuteAsync(
        ParticipantCredentials credentials,
        Func<ISessionUnitOfWork, SharedSession, SessionParticipant, DateTimeOffset, Task<Result>> action,
        CancellationToken cancellationToken)
    {
        // Cheap pre-check (also resolves the internal id for the lock) ...
        var pre = await _authorization.AuthorizeOwnerAsync(credentials, cancellationToken);
        if (!pre.Succeeded)
        {
            if (pre.Error == SessionError.Forbidden)
            {
                _logger.LogWarning("Rejected owner operation from non-owner participant {ParticipantId}", credentials?.ParticipantId);
            }

            return Result.Failure(pre.Error);
        }

        await using var sessionLock = await _locks.AcquireAsync(pre.Value!.SessionId, cancellationToken);
        await using var uow = _store.Begin();
        var session = await uow.FindByIdAsync(pre.Value.SessionId, cancellationToken);

        // ... and the authoritative check on the state we are about to mutate.
        var auth = _authorization.Authorize(session, credentials, requireOwner: true);
        if (!auth.Succeeded)
        {
            return Result.Failure(auth.Error);
        }

        return await action(uow, session!, auth.Value!, _time.GetUtcNow());
    }
}
