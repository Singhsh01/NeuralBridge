using Microsoft.Extensions.Logging.Abstractions;
using NeuralBridge.Application.Realtime;
using NeuralBridge.Infrastructure.Realtime;
using NeuralBridge.Tests.TestSupport;

namespace NeuralBridge.Tests.Realtime;

public class SessionEventBusTests
{
    private readonly InMemorySessionEventBus _bus = new(NullLogger<InMemorySessionEventBus>.Instance);

    [Fact]
    public async Task Subscribers_only_receive_their_own_session()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var receivedA = new List<SessionNotification>();
        var receivedB = new List<SessionNotification>();
        using var subA = _bus.Subscribe(a, n => Add(receivedA, n));
        using var subB = _bus.Subscribe(b, n => Add(receivedB, n));

        await _bus.PublishAsync(new ParticipantsChangedNotification(a));

        Assert.Single(receivedA);
        Assert.Empty(receivedB);
    }

    [Fact]
    public async Task Failing_subscriber_does_not_affect_others()
    {
        var id = Guid.NewGuid();
        var received = new List<SessionNotification>();
        using var bad = _bus.Subscribe(id, _ => throw new InvalidOperationException("boom"));
        using var good = _bus.Subscribe(id, n => Add(received, n));

        await _bus.PublishAsync(new ParticipantsChangedNotification(id));

        Assert.Single(received);
    }

    [Fact]
    public async Task Disposed_subscriptions_stop_receiving_and_are_cleaned_up()
    {
        var id = Guid.NewGuid();
        var received = new List<SessionNotification>();
        var sub = _bus.Subscribe(id, n => Add(received, n));
        sub.Dispose();
        sub.Dispose(); // idempotent

        await _bus.PublishAsync(new ParticipantsChangedNotification(id));

        Assert.Empty(received);
        Assert.Equal(0, _bus.SubscriberCount(id));
    }

    [Fact]
    public async Task Global_subscribers_receive_all_sessions()
    {
        var received = new List<SessionNotification>();
        using var all = _bus.SubscribeAll(n => Add(received, n));

        await _bus.PublishAsync(new ParticipantsChangedNotification(Guid.NewGuid()));
        await _bus.PublishAsync(new ParticipantsChangedNotification(Guid.NewGuid()));

        Assert.Equal(2, received.Count);
    }

    private static Task Add(List<SessionNotification> list, SessionNotification n)
    {
        lock (list)
        {
            list.Add(n);
        }

        return Task.CompletedTask;
    }
}

public class PresenceTrackerTests
{
    [Fact]
    public void Participant_is_online_while_any_connection_is_open()
    {
        var presence = new InMemoryPresenceTracker();
        var session = Guid.NewGuid();
        var participant = Guid.NewGuid();

        Assert.True(presence.Connect(session, participant, "c1"));
        Assert.False(presence.Connect(session, participant, "c2"));
        Assert.False(presence.Disconnect(session, participant, "c1"));
        Assert.Contains(participant, presence.GetOnlineParticipants(session));
        Assert.True(presence.Disconnect(session, participant, "c2"));
        Assert.Empty(presence.GetOnlineParticipants(session));
    }
}

public class SessionLockProviderTests
{
    [Fact]
    public async Task Lock_serializes_access_per_session_and_releases_resources()
    {
        var locks = new SessionLockProvider();
        var id = Guid.NewGuid();
        var inside = 0;
        var maxInside = 0;

        await Task.WhenAll(Enumerable.Range(0, 20).Select(async _ =>
        {
            await using var handle = await locks.AcquireAsync(id);
            var now = Interlocked.Increment(ref inside);
            maxInside = Math.Max(maxInside, now);
            await Task.Delay(1);
            Interlocked.Decrement(ref inside);
        }));

        Assert.Equal(1, maxInside);
        Assert.Equal(0, locks.ActiveLockCount);
    }

    [Fact]
    public async Task Different_sessions_do_not_block_each_other()
    {
        var locks = new SessionLockProvider();
        await using var a = await locks.AcquireAsync(Guid.NewGuid());
        var b = locks.AcquireAsync(Guid.NewGuid());
        Assert.True(b.IsCompleted);
        await (await b).DisposeAsync();
    }
}

public class LocationDisplayServiceTests
{
    [Fact]
    public async Task Coordinates_are_rounded_before_lookup()
    {
        using var app = new TestApp();
        var result = await app.Location.ResolveAsync(48.398765, 9.991234, TestApp.ClientKey);

        Assert.True(result.Succeeded);
        Assert.Equal("Ulm, Germany", result.Value!.Label);
        Assert.Equal((48.40, 9.99), app.Geocoder.LastQuery);
    }

    [Fact]
    public async Task Falls_back_to_rounded_coordinates_when_no_place_is_found()
    {
        using var app = new TestApp();
        app.Geocoder.Next = null;
        var result = await app.Location.ResolveAsync(-33.8688, 151.2093, TestApp.ClientKey);

        Assert.False(result.Value!.IsCityResolved);
        Assert.Equal("33.87° S, 151.21° E", result.Value.Label);
    }

    [Theory]
    [InlineData(91, 0)]
    [InlineData(0, 181)]
    [InlineData(double.NaN, 0)]
    public async Task Invalid_coordinates_are_rejected(double lat, double lon)
    {
        using var app = new TestApp();
        Assert.False((await app.Location.ResolveAsync(lat, lon, TestApp.ClientKey)).Succeeded);
        Assert.Equal(0, app.Geocoder.Calls);
    }

    [Fact]
    public async Task Results_are_cached_per_rounded_position()
    {
        using var app = new TestApp();
        await app.Location.ResolveAsync(48.3981, 9.9911, TestApp.ClientKey);
        await app.Location.ResolveAsync(48.4012, 9.9899, TestApp.ClientKey);
        Assert.Equal(1, app.Geocoder.Calls);
    }
}
