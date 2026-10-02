using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using NeuralBridge.Application.Realtime;

namespace NeuralBridge.Infrastructure.Realtime;

/// <summary>
/// Single-process publish/subscribe. Handlers run concurrently, and each one's failure is
/// logged and contained. For multi-instance deployments, replace this with a Redis- or
/// Azure SignalR-backed implementation of <see cref="ISessionEventBus"/>.
/// </summary>
public sealed class InMemorySessionEventBus : ISessionEventBus
{
    private readonly ConcurrentDictionary<Guid, ImmutableList<Subscription>> _bySession = new();
    private ImmutableList<Subscription> _global = [];
    private readonly ILogger<InMemorySessionEventBus> _logger;

    public InMemorySessionEventBus(ILogger<InMemorySessionEventBus> logger) => _logger = logger;

    public async Task PublishAsync(SessionNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        var handlers = Volatile.Read(ref _global);
        if (_bySession.TryGetValue(notification.SessionId, out var sessionHandlers))
        {
            handlers = handlers.AddRange(sessionHandlers);
        }

        if (handlers.IsEmpty)
        {
            return;
        }

        await Task.WhenAll(handlers.Select(h => InvokeSafelyAsync(h, notification)));
    }

    public IDisposable Subscribe(Guid sessionId, Func<SessionNotification, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var subscription = new Subscription(handler, s => Unsubscribe(sessionId, s));
        _bySession.AddOrUpdate(sessionId, _ => [subscription], (_, list) => list.Add(subscription));
        return subscription;
    }

    public IDisposable SubscribeAll(Func<SessionNotification, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var subscription = new Subscription(handler, s => ImmutableInterlocked.Update(ref _global, list => list.Remove(s)));
        ImmutableInterlocked.Update(ref _global, list => list.Add(subscription));
        return subscription;
    }

    /// <summary>Number of per-session subscriptions (diagnostics/tests).</summary>
    public int SubscriberCount(Guid sessionId) => _bySession.TryGetValue(sessionId, out var list) ? list.Count : 0;

    private void Unsubscribe(Guid sessionId, Subscription subscription)
    {
        while (_bySession.TryGetValue(sessionId, out var list))
        {
            var updated = list.Remove(subscription);
            var replaced = updated.IsEmpty
                ? _bySession.TryRemove(new KeyValuePair<Guid, ImmutableList<Subscription>>(sessionId, list))
                : _bySession.TryUpdate(sessionId, updated, list);
            if (replaced)
            {
                return;
            }
        }
    }

    private async Task InvokeSafelyAsync(Subscription subscription, SessionNotification notification)
    {
        try
        {
            await subscription.Handler(notification);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Session notification handler failed for {NotificationType}", notification.GetType().Name);
        }
    }

    private sealed class Subscription : IDisposable
    {
        private readonly Action<Subscription> _onDispose;
        private int _disposed;

        public Subscription(Func<SessionNotification, Task> handler, Action<Subscription> onDispose)
        {
            Handler = handler;
            _onDispose = onDispose;
        }

        public Func<SessionNotification, Task> Handler { get; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _onDispose(this);
            }
        }
    }
}
