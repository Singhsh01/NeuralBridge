using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NeuralBridge.Application.Abstractions;
using NeuralBridge.Application.Common;
using NeuralBridge.Application.Options;
using NeuralBridge.Application.Realtime;
using NeuralBridge.Application.Sessions;

namespace NeuralBridge.Application.Calls;

public interface ICallService
{
    Task<Result<IReadOnlyList<CallRosterEntry>>> JoinAsync(ParticipantCredentials credentials, bool audio, bool video, CancellationToken cancellationToken = default);

    Task<Result> UpdateMediaAsync(ParticipantCredentials credentials, bool audio, bool video, CancellationToken cancellationToken = default);

    Task<Result> LeaveAsync(ParticipantCredentials credentials, CancellationToken cancellationToken = default);

    /// <summary>Removes a member without credentials (tab closed, participant removed, session ended).</summary>
    Task LeaveByParticipantAsync(Guid sessionId, Guid participantId);

    /// <summary>
    /// Relays one opaque WebRTC signaling message (offer/answer/ICE candidate) to another member
    /// of the same call. Both ends must currently be call members of the same session.
    /// </summary>
    Task<Result> SignalAsync(ParticipantCredentials credentials, Guid toParticipantId, string payload, CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<CallRosterEntry>>> GetRosterAsync(ParticipantCredentials credentials, CancellationToken cancellationToken = default);

    CallOptions Options { get; }
}

public sealed class CallService : ICallService
{
    private readonly ISessionStore _store;
    private readonly ISessionAuthorizationService _authorization;
    private readonly ICallRegistry _registry;
    private readonly ISessionEventBus _bus;
    private readonly IAppRateLimiter _rateLimiter;
    private readonly TimeProvider _time;
    private readonly ILogger<CallService> _logger;

    public CallService(
        ISessionStore store,
        ISessionAuthorizationService authorization,
        ICallRegistry registry,
        ISessionEventBus bus,
        IAppRateLimiter rateLimiter,
        TimeProvider time,
        IOptions<CallOptions> options,
        ILogger<CallService> logger)
    {
        _store = store;
        _authorization = authorization;
        _registry = registry;
        _bus = bus;
        _rateLimiter = rateLimiter;
        _time = time;
        Options = options.Value;
        _logger = logger;
    }

    public CallOptions Options { get; }

    public async Task<Result<IReadOnlyList<CallRosterEntry>>> JoinAsync(ParticipantCredentials credentials, bool audio, bool video, CancellationToken cancellationToken = default)
    {
        if (!Options.Enabled)
        {
            return Result<IReadOnlyList<CallRosterEntry>>.Failure(SessionError.Forbidden, "Calls are turned off on this server.");
        }

        var auth = await _authorization.AuthorizeAsync(credentials, cancellationToken);
        if (!auth.Succeeded)
        {
            return Result<IReadOnlyList<CallRosterEntry>>.Failure(auth.Error);
        }

        var me = auth.Value!;
        var joinedAt = _registry.Members(me.SessionId).FirstOrDefault(m => m.ParticipantId == me.ParticipantId)?.JoinedAt ?? _time.GetUtcNow();
        if (!_registry.Upsert(me.SessionId, new CallMemberState(me.ParticipantId, audio, video, joinedAt), Options.MaxCallParticipants))
        {
            return Result<IReadOnlyList<CallRosterEntry>>.Failure(
                SessionError.CapacityReached,
                $"This call is full ({Options.MaxCallParticipants} people). You can still use the shared text.");
        }

        _logger.LogInformation("Participant {ParticipantId} joined the call in session {SessionId}", me.ParticipantId, me.SessionId);
        await _bus.PublishAsync(new CallStateChangedNotification(me.SessionId));
        return await RosterAsync(me.SessionId, me.ParticipantId, cancellationToken);
    }

    public async Task<Result> UpdateMediaAsync(ParticipantCredentials credentials, bool audio, bool video, CancellationToken cancellationToken = default)
    {
        var auth = await _authorization.AuthorizeAsync(credentials, cancellationToken);
        if (!auth.Succeeded)
        {
            return Result.Failure(auth.Error);
        }

        var me = auth.Value!;
        var current = _registry.Members(me.SessionId).FirstOrDefault(m => m.ParticipantId == me.ParticipantId);
        if (current is null)
        {
            return Result.Failure(SessionError.NotFound, "You're not in the call.");
        }

        _registry.Upsert(me.SessionId, current with { AudioEnabled = audio, VideoEnabled = video }, Options.MaxCallParticipants);
        await _bus.PublishAsync(new CallStateChangedNotification(me.SessionId));
        return Result.Success();
    }

    public async Task<Result> LeaveAsync(ParticipantCredentials credentials, CancellationToken cancellationToken = default)
    {
        // Leaving must work even after access ended (session closed, removed), so resolve the session without full authorization.
        await using var uow = _store.Begin();
        var session = await uow.FindByPublicIdAsync(credentials.PublicId, cancellationToken);
        if (session is null)
        {
            return Result.Success();
        }

        var auth = _authorization.Authorize(session, credentials, requireOwner: false);
        if (auth.Error == SessionError.Unauthorized)
        {
            return Result.Failure(SessionError.Unauthorized);
        }

        await LeaveByParticipantAsync(session.Id, credentials.ParticipantId);
        return Result.Success();
    }

    public async Task LeaveByParticipantAsync(Guid sessionId, Guid participantId)
    {
        if (_registry.Remove(sessionId, participantId))
        {
            _logger.LogInformation("Participant {ParticipantId} left the call in session {SessionId}", participantId, sessionId);
            await _bus.PublishAsync(new CallStateChangedNotification(sessionId));
        }
    }

    public async Task<Result> SignalAsync(ParticipantCredentials credentials, Guid toParticipantId, string payload, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(payload) || payload.Length > Options.MaxSignalBytes)
        {
            return Result.Failure(SessionError.ValidationFailed, "Invalid call signal.");
        }

        if (!_rateLimiter.TryAcquire(RateLimitPolicy.CallSignal, credentials.ParticipantId.ToString("N")))
        {
            return Result.Failure(SessionError.RateLimited);
        }

        var auth = await _authorization.AuthorizeAsync(credentials, cancellationToken);
        if (!auth.Succeeded)
        {
            return Result.Failure(auth.Error);
        }

        var me = auth.Value!;
        if (toParticipantId == me.ParticipantId ||
            !_registry.Contains(me.SessionId, me.ParticipantId) ||
            !_registry.Contains(me.SessionId, toParticipantId))
        {
            // The target is not in *this* session's call: never relay across sessions.
            return Result.Failure(SessionError.Forbidden, "That person isn't in this call.");
        }

        await _bus.PublishAsync(new CallSignalNotification(me.SessionId, me.ParticipantId, toParticipantId, payload));
        return Result.Success();
    }

    public async Task<Result<IReadOnlyList<CallRosterEntry>>> GetRosterAsync(ParticipantCredentials credentials, CancellationToken cancellationToken = default)
    {
        var auth = await _authorization.AuthorizeAsync(credentials, cancellationToken);
        return auth.Succeeded
            ? await RosterAsync(auth.Value!.SessionId, auth.Value.ParticipantId, cancellationToken)
            : Result<IReadOnlyList<CallRosterEntry>>.Failure(auth.Error);
    }

    private async Task<Result<IReadOnlyList<CallRosterEntry>>> RosterAsync(Guid sessionId, Guid me, CancellationToken cancellationToken)
    {
        await using var uow = _store.Begin();
        var session = await uow.FindByIdAsync(sessionId, cancellationToken);
        var members = _registry.Members(sessionId);
        IReadOnlyList<CallRosterEntry> roster = members
            .OrderBy(m => m.JoinedAt)
            .Select(m =>
            {
                var participant = session?.FindParticipant(m.ParticipantId);
                return new CallRosterEntry(m.ParticipantId, participant?.DisplayName ?? "Someone", m.AudioEnabled, m.VideoEnabled, m.ParticipantId == me);
            })
            .ToList();
        return Result<IReadOnlyList<CallRosterEntry>>.Success(roster);
    }
}
