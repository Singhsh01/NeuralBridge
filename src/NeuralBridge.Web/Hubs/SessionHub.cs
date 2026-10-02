using Microsoft.AspNetCore.SignalR;
using NeuralBridge.Application.Abstractions;
using NeuralBridge.Application.Common;
using NeuralBridge.Application.Documents;
using NeuralBridge.Application.Realtime;
using NeuralBridge.Application.Sessions;

namespace NeuralBridge.Web.Hubs;

/// <summary>Server → client messages of <see cref="SessionHub"/>.</summary>
public interface ISessionHubClient
{
    Task DocumentChanged(HubDocument document);

    Task Typing(string displayName);

    Task ParticipantsChanged();

    Task SessionSettingsChanged();

    Task SessionEnded(string reason);

    Task Removed();
}

public sealed record HubDocument(string Content, long Version, DateTimeOffset UpdatedAt, string? EditorName);

public sealed record HubAttachResult(string Code, DateTimeOffset ExpiresAt, bool IsOwner, bool CanEdit, HubDocument Document);

/// <summary>
/// Dedicated real-time endpoint for non-Blazor clients (scripts, future native apps).
/// <para>
/// Isolation model: a connection binds to exactly one session through <see cref="Attach"/>,
/// which validates the participant's bearer token. The binding is stored server-side in
/// <see cref="HubCallerContext.Items"/>. No later method accepts a session id from the client,
/// so a connection can never read from or write to another session's group.
/// </para>
/// </summary>
public sealed class SessionHub : Hub<ISessionHubClient>
{
    internal const string BindingKey = "nb.binding";

    private readonly ISessionAuthorizationService _authorization;
    private readonly ITextSynchronizationService _sync;
    private readonly ISessionService _sessions;
    private readonly IPresenceTracker _presence;
    private readonly ISessionEventBus _bus;
    private readonly HubConnectionRegistry _registry;
    private readonly ILogger<SessionHub> _logger;

    public SessionHub(
        ISessionAuthorizationService authorization,
        ITextSynchronizationService sync,
        ISessionService sessions,
        IPresenceTracker presence,
        ISessionEventBus bus,
        HubConnectionRegistry registry,
        ILogger<SessionHub> logger)
    {
        _authorization = authorization;
        _sync = sync;
        _sessions = sessions;
        _presence = presence;
        _bus = bus;
        _registry = registry;
        _logger = logger;
    }

    public static string GroupName(Guid sessionId) => $"session:{sessionId:N}";

    public async Task<HubAttachResult> Attach(string publicId, Guid participantId, string token)
    {
        if (Context.Items.ContainsKey(BindingKey))
        {
            throw new HubException("This connection is already attached to a session.");
        }

        var credentials = new ParticipantCredentials(publicId ?? string.Empty, participantId, token ?? string.Empty);
        var auth = await _authorization.AuthorizeAsync(credentials, Context.ConnectionAborted);
        if (!auth.Succeeded)
        {
            throw new HubException(ErrorMessages.For(auth.Error));
        }

        var participant = auth.Value!;
        var document = await _sync.GetDocumentAsync(credentials, Context.ConnectionAborted);
        if (!document.Succeeded)
        {
            throw new HubException(ErrorMessages.For(document.Error));
        }

        Context.Items[BindingKey] = new HubBinding(participant.SessionId, credentials);
        await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(participant.SessionId), Context.ConnectionAborted);
        _registry.Register(Context, participant.SessionId, participant.ParticipantId);
        if (_presence.Connect(participant.SessionId, participant.ParticipantId, Context.ConnectionId))
        {
            await _bus.PublishAsync(new ParticipantsChangedNotification(participant.SessionId));
        }

        _logger.LogInformation("Hub connection attached to session {SessionId}", participant.SessionId);
        var snapshot = (await _sessions.GetSnapshotAsync(credentials, Context.ConnectionAborted)).Value;
        var d = document.Value!;
        return new HubAttachResult(
            snapshot?.FormattedCode ?? string.Empty,
            snapshot?.ExpiresAt ?? DateTimeOffset.MinValue,
            participant.IsOwner,
            participant.CanEdit,
            new HubDocument(d.Content, d.Version, d.UpdatedAt, d.LastEditorName));
    }

    /// <returns>The new document version.</returns>
    public async Task<long> UpdateText(string content, long baseVersion)
    {
        var binding = RequireBinding();
        var result = await _sync.UpdateAsync(binding.Credentials, content, baseVersion, Context.ConnectionId, Context.ConnectionAborted);
        if (!result.Succeeded)
        {
            throw new HubException(result.Message ?? ErrorMessages.For(result.Error));
        }

        return result.Value!.Version;
    }

    public async Task Typing()
    {
        var binding = RequireBinding();
        await _sync.NotifyTypingAsync(binding.Credentials, Context.ConnectionId, Context.ConnectionAborted);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _registry.Unregister(Context.ConnectionId);
        if (Context.Items.TryGetValue(BindingKey, out var value) && value is HubBinding binding &&
            _presence.Disconnect(binding.SessionId, binding.Credentials.ParticipantId, Context.ConnectionId))
        {
            await _bus.PublishAsync(new ParticipantsChangedNotification(binding.SessionId));
        }

        await base.OnDisconnectedAsync(exception);
    }

    private HubBinding RequireBinding() =>
        Context.Items.TryGetValue(BindingKey, out var value) && value is HubBinding binding
            ? binding
            : throw new HubException("Attach to a session first.");

    internal sealed record HubBinding(Guid SessionId, ParticipantCredentials Credentials);
}
