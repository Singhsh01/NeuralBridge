using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NeuralBridge.Application.Abstractions;
using NeuralBridge.Application.Common;
using NeuralBridge.Application.Options;
using NeuralBridge.Application.Realtime;
using NeuralBridge.Application.Sessions;
using NeuralBridge.Domain.Sessions;

namespace NeuralBridge.Application.Documents;

public sealed record DocumentView(string Content, long Version, DateTimeOffset UpdatedAt, string? LastEditorName);

/// <param name="Version">The document version after the update.</param>
/// <param name="Changed">False when the submitted text equalled the current text (no broadcast).</param>
public sealed record DocumentUpdateResult(long Version, bool Changed, bool OverwroteConcurrentChange);

public interface ITextSynchronizationService
{
    Task<Result<DocumentView>> GetDocumentAsync(ParticipantCredentials credentials, CancellationToken cancellationToken = default);

    /// <param name="originId">Opaque id of the sender's circuit/connection, echoed in the broadcast so the sender can ignore it.</param>
    Task<Result<DocumentUpdateResult>> UpdateAsync(
        ParticipantCredentials credentials,
        string? content,
        long baseVersion,
        string? originId,
        CancellationToken cancellationToken = default);

    Task<Result<DocumentUpdateResult>> ClearAsync(ParticipantCredentials credentials, string? originId, CancellationToken cancellationToken = default);

    Task<Result> NotifyTypingAsync(ParticipantCredentials credentials, string? originId, CancellationToken cancellationToken = default);
}

public sealed class TextSynchronizationService : ITextSynchronizationService
{
    private readonly ISessionStore _store;
    private readonly ISessionAuthorizationService _authorization;
    private readonly IDocumentStore _documents;
    private readonly IDocumentMergeStrategy _merge;
    private readonly ISessionEventBus _bus;
    private readonly IAppRateLimiter _rateLimiter;
    private readonly TimeProvider _time;
    private readonly SessionOptions _options;
    private readonly ILogger<TextSynchronizationService> _logger;

    public TextSynchronizationService(
        ISessionStore store,
        ISessionAuthorizationService authorization,
        IDocumentStore documents,
        IDocumentMergeStrategy merge,
        ISessionEventBus bus,
        IAppRateLimiter rateLimiter,
        TimeProvider time,
        IOptions<SessionOptions> options,
        ILogger<TextSynchronizationService> logger)
    {
        _store = store;
        _authorization = authorization;
        _documents = documents;
        _merge = merge;
        _bus = bus;
        _rateLimiter = rateLimiter;
        _time = time;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<Result<DocumentView>> GetDocumentAsync(ParticipantCredentials credentials, CancellationToken cancellationToken = default)
    {
        await using var uow = _store.Begin();
        var session = await uow.FindByPublicIdAsync(credentials.PublicId, cancellationToken);
        var auth = _authorization.Authorize(session, credentials, requireOwner: false);
        if (!auth.Succeeded)
        {
            return Result<DocumentView>.Failure(auth.Error);
        }

        var document = _documents.GetOrCreate(session!.Id, _time.GetUtcNow());
        return Result<DocumentView>.Success(new DocumentView(
            document.Content,
            document.Version,
            document.UpdatedAt,
            ResolveName(session, document.LastEditorParticipantId)));
    }

    public async Task<Result<DocumentUpdateResult>> UpdateAsync(
        ParticipantCredentials credentials,
        string? content,
        long baseVersion,
        string? originId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        content ??= string.Empty;
        if (content.Length > _options.MaxContentLength)
        {
            return Result<DocumentUpdateResult>.Failure(
                SessionError.ValidationFailed,
                $"The text is longer than the {_options.MaxContentLength:N0}-character limit.");
        }

        if (!_rateLimiter.TryAcquire(RateLimitPolicy.DocumentUpdate, credentials.ParticipantId.ToString("N")))
        {
            return Result<DocumentUpdateResult>.Failure(SessionError.RateLimited);
        }

        await using var uow = _store.Begin();
        var session = await uow.FindByPublicIdAsync(credentials.PublicId, cancellationToken);
        var auth = _authorization.Authorize(session, credentials, requireOwner: false);
        if (!auth.Succeeded)
        {
            return Result<DocumentUpdateResult>.Failure(auth.Error);
        }

        var participant = auth.Value!;
        if (!session!.CanEdit(participant))
        {
            return Result<DocumentUpdateResult>.Failure(SessionError.ReadOnly);
        }

        var now = _time.GetUtcNow();
        var (document, outcome) = _documents.Update(session.Id, now, current =>
        {
            if (string.Equals(current.Content, content, StringComparison.Ordinal))
            {
                return (current, (current, new DocumentUpdateResult(current.Version, Changed: false, OverwroteConcurrentChange: false)));
            }

            var merged = _merge.Merge(current, new DocumentEdit(content, baseVersion, participant.Id), now);
            var result = new DocumentUpdateResult(merged.Document.Version, Changed: true, merged.OverwroteConcurrentChange);
            return (merged.Document, (merged.Document, result));
        });

        if (outcome.Changed)
        {
            if (outcome.OverwroteConcurrentChange)
            {
                _logger.LogDebug("Session {SessionId}: last-write-wins replaced a concurrent edit", session.Id);
            }

            await _bus.PublishAsync(new DocumentChangedNotification(
                session.Id,
                document.Content,
                outcome.Version,
                document.UpdatedAt,
                participant.Id,
                participant.DisplayName,
                originId));
        }

        return Result<DocumentUpdateResult>.Success(outcome);
    }

    public async Task<Result<DocumentUpdateResult>> ClearAsync(ParticipantCredentials credentials, string? originId, CancellationToken cancellationToken = default)
    {
        var auth = await _authorization.AuthorizeAsync(credentials, cancellationToken);
        if (!auth.Succeeded)
        {
            return Result<DocumentUpdateResult>.Failure(auth.Error);
        }

        var result = await UpdateAsync(credentials, string.Empty, long.MaxValue, originId, cancellationToken);
        if (result.Succeeded && result.Value!.Changed)
        {
            // Event-only write: the aggregate is deliberately not loaded, so this save can never
            // overwrite a concurrent, lock-protected session mutation.
            await using var uow = _store.Begin();
            uow.AddEvent(new SessionEvent(auth.Value!.SessionId, SessionEventType.ContentCleared, _time.GetUtcNow(), credentials.ParticipantId));
            await uow.SaveChangesAsync(cancellationToken);
        }

        return result;
    }

    public async Task<Result> NotifyTypingAsync(ParticipantCredentials credentials, string? originId, CancellationToken cancellationToken = default)
    {
        var auth = await _authorization.AuthorizeAsync(credentials, cancellationToken);
        if (!auth.Succeeded)
        {
            return Result.Failure(auth.Error);
        }

        var context = auth.Value!;
        if (!context.CanEdit)
        {
            return Result.Failure(SessionError.ReadOnly);
        }

        await _bus.PublishAsync(new TypingNotification(context.SessionId, context.ParticipantId, context.DisplayName, originId));
        return Result.Success();
    }

    private static string? ResolveName(SharedSession session, Guid? participantId) =>
        participantId is { } id ? session.FindParticipant(id)?.DisplayName : null;
}
