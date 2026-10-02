using NeuralBridge.Application.Abstractions;
using NeuralBridge.Application.Common;
using NeuralBridge.Domain.Sessions;

namespace NeuralBridge.Application.Sessions;

public interface ISessionAuthorizationService
{
    /// <summary>Validates credentials against the current session state. Every session operation calls this.</summary>
    Task<Result<ParticipantContext>> AuthorizeAsync(ParticipantCredentials? credentials, CancellationToken cancellationToken = default);

    Task<Result<ParticipantContext>> AuthorizeOwnerAsync(ParticipantCredentials? credentials, CancellationToken cancellationToken = default);

    /// <summary>
    /// Pure check against an already-loaded aggregate (used inside units of work so the
    /// decision and the mutation see the same state).
    /// </summary>
    Result<SessionParticipant> Authorize(SharedSession? session, ParticipantCredentials? credentials, bool requireOwner);
}

public sealed class SessionAuthorizationService : ISessionAuthorizationService
{
    private readonly ISessionStore _store;
    private readonly ITokenService _tokens;
    private readonly TimeProvider _time;

    public SessionAuthorizationService(ISessionStore store, ITokenService tokens, TimeProvider time)
    {
        _store = store;
        _tokens = tokens;
        _time = time;
    }

    public async Task<Result<ParticipantContext>> AuthorizeAsync(ParticipantCredentials? credentials, CancellationToken cancellationToken = default) =>
        await AuthorizeCoreAsync(credentials, requireOwner: false, cancellationToken);

    public async Task<Result<ParticipantContext>> AuthorizeOwnerAsync(ParticipantCredentials? credentials, CancellationToken cancellationToken = default) =>
        await AuthorizeCoreAsync(credentials, requireOwner: true, cancellationToken);

    public Result<SessionParticipant> Authorize(SharedSession? session, ParticipantCredentials? credentials, bool requireOwner)
    {
        if (session is null || credentials is null)
        {
            return Result<SessionParticipant>.Failure(SessionError.Unauthorized);
        }

        var participant = session.FindParticipant(credentials.ParticipantId);

        // Token check first: unknown participants and wrong tokens are indistinguishable.
        if (participant is null || !_tokens.Matches(credentials.Token, participant.TokenHash))
        {
            return Result<SessionParticipant>.Failure(SessionError.Unauthorized);
        }

        var status = session.GetStatus(_time.GetUtcNow());
        if (status == SessionStatus.Expired)
        {
            return Result<SessionParticipant>.Failure(SessionError.Expired);
        }

        if (status == SessionStatus.Closed)
        {
            return Result<SessionParticipant>.Failure(SessionError.Closed);
        }

        if (participant.IsRemoved)
        {
            return Result<SessionParticipant>.Failure(SessionError.Removed);
        }

        if (!participant.IsActive)
        {
            return Result<SessionParticipant>.Failure(SessionError.Unauthorized);
        }

        if (requireOwner && !participant.IsOwner)
        {
            return Result<SessionParticipant>.Failure(SessionError.Forbidden);
        }

        return Result<SessionParticipant>.Success(participant);
    }

    private async Task<Result<ParticipantContext>> AuthorizeCoreAsync(ParticipantCredentials? credentials, bool requireOwner, CancellationToken cancellationToken)
    {
        if (credentials is null || string.IsNullOrEmpty(credentials.PublicId) || string.IsNullOrEmpty(credentials.Token))
        {
            return Result<ParticipantContext>.Failure(SessionError.Unauthorized);
        }

        await using var uow = _store.Begin();
        var session = await uow.FindByPublicIdAsync(credentials.PublicId, cancellationToken);
        var result = Authorize(session, credentials, requireOwner);
        if (!result.Succeeded)
        {
            return Result<ParticipantContext>.Failure(result.Error);
        }

        var participant = result.Value!;
        return Result<ParticipantContext>.Success(new ParticipantContext(
            session!.Id,
            session.PublicId,
            participant.Id,
            participant.DisplayName,
            participant.IsOwner,
            session.CanEdit(participant)));
    }
}
