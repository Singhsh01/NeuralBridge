using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using NeuralBridge.Application.Realtime;
using NeuralBridge.Domain.Sessions;

namespace NeuralBridge.Web.Hubs;

/// <summary>Tracks live hub connections so the relay can disconnect removed participants and ended sessions.</summary>
public sealed class HubConnectionRegistry
{
    private readonly ConcurrentDictionary<string, Entry> _connections = new(StringComparer.Ordinal);

    public void Register(HubCallerContext context, Guid sessionId, Guid participantId) =>
        _connections[context.ConnectionId] = new Entry(context, sessionId, participantId);

    public void Unregister(string connectionId) => _connections.TryRemove(connectionId, out _);

    public IReadOnlyList<HubCallerContext> ForSession(Guid sessionId) =>
        _connections.Values.Where(e => e.SessionId == sessionId).Select(e => e.Context).ToList();

    public IReadOnlyList<HubCallerContext> ForParticipant(Guid sessionId, Guid participantId) =>
        _connections.Values.Where(e => e.SessionId == sessionId && e.ParticipantId == participantId).Select(e => e.Context).ToList();

    private sealed record Entry(HubCallerContext Context, Guid SessionId, Guid ParticipantId);
}

/// <summary>
/// Forwards session notifications from the in-process bus to SignalR groups. Every message
/// goes to exactly one group (<c>session:{id}</c>). Document changes skip the originating
/// connection, so a writer never receives its own echo.
/// </summary>
public sealed class HubNotificationRelay : IHostedService
{
    private readonly ISessionEventBus _bus;
    private readonly IHubContext<SessionHub, ISessionHubClient> _hub;
    private readonly HubConnectionRegistry _registry;
    private readonly ILogger<HubNotificationRelay> _logger;
    private IDisposable? _subscription;

    public HubNotificationRelay(
        ISessionEventBus bus,
        IHubContext<SessionHub, ISessionHubClient> hub,
        HubConnectionRegistry registry,
        ILogger<HubNotificationRelay> logger)
    {
        _bus = bus;
        _hub = hub;
        _registry = registry;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = _bus.SubscribeAll(RelayAsync);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        return Task.CompletedTask;
    }

    internal async Task RelayAsync(SessionNotification notification)
    {
        var group = SessionHub.GroupName(notification.SessionId);
        switch (notification)
        {
            case DocumentChangedNotification changed:
                var document = new HubDocument(changed.Content, changed.Version, changed.UpdatedAt, changed.EditorName);
                var target = changed.OriginId is { } origin
                    ? _hub.Clients.GroupExcept(group, origin)
                    : _hub.Clients.Group(group);
                await target.DocumentChanged(document);
                break;

            case TypingNotification typing:
                var typingTarget = typing.OriginId is { } typingOrigin
                    ? _hub.Clients.GroupExcept(group, typingOrigin)
                    : _hub.Clients.Group(group);
                await typingTarget.Typing(typing.DisplayName);
                break;

            case ParticipantsChangedNotification:
                await _hub.Clients.Group(group).ParticipantsChanged();
                break;

            case SessionSettingsChangedNotification:
                await _hub.Clients.Group(group).SessionSettingsChanged();
                break;

            case ParticipantRemovedNotification removed:
                foreach (var connection in _registry.ForParticipant(removed.SessionId, removed.ParticipantId))
                {
                    await _hub.Clients.Client(connection.ConnectionId).Removed();
                    connection.Abort();
                }

                break;

            case SessionEndedNotification ended:
                await _hub.Clients.Group(group).SessionEnded(ended.Reason == SessionEndReason.Expired ? "expired" : "closed");
                foreach (var connection in _registry.ForSession(ended.SessionId))
                {
                    connection.Abort();
                }

                _logger.LogDebug("Disconnected hub clients of ended session {SessionId}", ended.SessionId);
                break;
        }
    }
}
