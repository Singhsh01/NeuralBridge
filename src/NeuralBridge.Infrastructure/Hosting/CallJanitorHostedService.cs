using Microsoft.Extensions.Hosting;
using NeuralBridge.Application.Calls;
using NeuralBridge.Application.Realtime;

namespace NeuralBridge.Infrastructure.Hosting;

/// <summary>Keeps call membership consistent with session membership: removed participants and ended sessions leave the call.</summary>
public sealed class CallJanitorHostedService : IHostedService
{
    private readonly ISessionEventBus _bus;
    private readonly ICallService _calls;
    private readonly ICallRegistry _registry;
    private IDisposable? _subscription;

    public CallJanitorHostedService(ISessionEventBus bus, ICallService calls, ICallRegistry registry)
    {
        _bus = bus;
        _calls = calls;
        _registry = registry;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = _bus.SubscribeAll(n => n switch
        {
            ParticipantRemovedNotification removed => _calls.LeaveByParticipantAsync(removed.SessionId, removed.ParticipantId),
            SessionEndedNotification ended => Clear(ended.SessionId),
            _ => Task.CompletedTask,
        });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        return Task.CompletedTask;
    }

    private Task Clear(Guid sessionId)
    {
        _registry.ClearSession(sessionId);
        return Task.CompletedTask;
    }
}
