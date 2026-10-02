using NeuralBridge.Application.Abstractions;
using NeuralBridge.Application.Calls;
using NeuralBridge.Application.Realtime;

namespace NeuralBridge.Web.Components.Session;

/// <summary>
/// Scoped per circuit. It marks the workspace's participant online while the circuit's
/// connection is up. The circuit handler flips it when the browser disconnects or comes
/// back, so other participants see presence changes immediately, not when the circuit is
/// finally evicted.
/// </summary>
public sealed class WorkspacePresence
{
    private readonly IPresenceTracker _presence;
    private readonly ISessionEventBus _bus;
    private readonly ICallService _calls;
    private Registration? _registration;
    private bool _connected = true;

    public WorkspacePresence(IPresenceTracker presence, ISessionEventBus bus, ICallService calls)
    {
        _presence = presence;
        _bus = bus;
        _calls = calls;
    }

    public async Task EnterAsync(Guid sessionId, Guid participantId, string connectionId)
    {
        await LeaveAsync();
        _registration = new Registration(sessionId, participantId, connectionId);
        if (_connected)
        {
            await ConnectAsync(_registration);
        }
    }

    public async Task LeaveAsync()
    {
        if (_registration is { } r)
        {
            _registration = null;
            await DisconnectAsync(r);

            // Leaving the workspace (navigation, tab closed, circuit evicted) also leaves the call.
            await _calls.LeaveByParticipantAsync(r.SessionId, r.ParticipantId);
        }
    }

    internal async Task OnConnectionChangedAsync(bool connected)
    {
        _connected = connected;
        if (_registration is not { } r)
        {
            return;
        }

        if (connected)
        {
            await ConnectAsync(r);
        }
        else
        {
            await DisconnectAsync(r);
        }
    }

    private async Task ConnectAsync(Registration r)
    {
        if (_presence.Connect(r.SessionId, r.ParticipantId, r.ConnectionId))
        {
            await _bus.PublishAsync(new ParticipantsChangedNotification(r.SessionId));
        }
    }

    private async Task DisconnectAsync(Registration r)
    {
        if (_presence.Disconnect(r.SessionId, r.ParticipantId, r.ConnectionId))
        {
            await _bus.PublishAsync(new ParticipantsChangedNotification(r.SessionId));
        }
    }

    private sealed record Registration(Guid SessionId, Guid ParticipantId, string ConnectionId);
}
