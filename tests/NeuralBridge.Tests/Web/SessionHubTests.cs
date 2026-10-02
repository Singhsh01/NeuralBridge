using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using NeuralBridge.Application.Realtime;
using NeuralBridge.Domain.Sessions;
using NeuralBridge.Tests.TestSupport;
using NeuralBridge.Web.Hubs;

namespace NeuralBridge.Tests.Web;

public class SessionHubTests
{
    private static (SessionHub Hub, FakeCallerContext Context, RecordingGroups Groups) NewHub(TestApp app, HubConnectionRegistry registry, string connectionId)
    {
        var context = new FakeCallerContext(connectionId);
        var groups = new RecordingGroups();
        var hub = new SessionHub(app.Authorization, app.Sync, app.Sessions, app.Presence, app.Bus, registry, NullLogger<SessionHub>.Instance)
        {
            Context = context,
            Groups = groups,
        };
        return (hub, context, groups);
    }

    [Fact]
    public async Task Attach_joins_only_the_sessions_own_group()
    {
        using var app = new TestApp();
        var a = await app.CreateSessionAsync();
        await app.CreateSessionAsync(); // another session that must stay isolated
        var registry = new HubConnectionRegistry();
        var (hub, _, groups) = NewHub(app, registry, "conn-a");

        var result = await hub.Attach(a.PublicId, a.Credentials.ParticipantId, a.Credentials.Token);

        var sessionId = (await app.LoadAsync(a.PublicId)).Id;
        var membership = Assert.Single(groups.Added);
        Assert.Equal(("conn-a", SessionHub.GroupName(sessionId)), membership);
        Assert.Equal(a.FormattedCode, result.Code);
        Assert.True(result.IsOwner);
    }

    [Fact]
    public async Task Invalid_or_cross_session_credentials_cannot_attach()
    {
        using var app = new TestApp();
        var a = await app.CreateSessionAsync();
        var b = await app.CreateSessionAsync();
        var registry = new HubConnectionRegistry();

        var (forged, _, forgedGroups) = NewHub(app, registry, "c1");
        await Assert.ThrowsAsync<HubException>(() => forged.Attach(a.PublicId, a.Credentials.ParticipantId, "forged"));

        var (cross, _, crossGroups) = NewHub(app, registry, "c2");
        await Assert.ThrowsAsync<HubException>(() => cross.Attach(a.PublicId, b.Credentials.ParticipantId, b.Credentials.Token));

        Assert.Empty(forgedGroups.Added);
        Assert.Empty(crossGroups.Added);
    }

    [Fact]
    public async Task Methods_require_attachment_and_use_the_server_side_binding()
    {
        using var app = new TestApp();
        var a = await app.CreateSessionAsync();
        var b = await app.CreateSessionAsync();
        var registry = new HubConnectionRegistry();
        var (hub, _, _) = NewHub(app, registry, "conn");

        await Assert.ThrowsAsync<HubException>(() => hub.UpdateText("x", 0));

        await hub.Attach(a.PublicId, a.Credentials.ParticipantId, a.Credentials.Token);
        var version = await hub.UpdateText("only A", 0);

        Assert.Equal(1, version);
        Assert.Equal("only A", (await app.Sync.GetDocumentAsync(a.Credentials)).Value!.Content);
        Assert.Equal(string.Empty, (await app.Sync.GetDocumentAsync(b.Credentials)).Value!.Content);
        await Assert.ThrowsAsync<HubException>(() => hub.Attach(b.PublicId, b.Credentials.ParticipantId, b.Credentials.Token));
    }

    [Fact]
    public async Task Relay_targets_one_group_and_excludes_the_origin()
    {
        using var app = new TestApp();
        var hubContext = new RecordingHubContext();
        var relay = new HubNotificationRelay(app.Bus, hubContext, new HubConnectionRegistry(), NullLogger<HubNotificationRelay>.Instance);
        var sessionId = Guid.NewGuid();

        await relay.RelayAsync(new DocumentChangedNotification(sessionId, "text", 3, DateTimeOffset.UtcNow, Guid.NewGuid(), "Laptop", "conn-origin"));
        await relay.RelayAsync(new ParticipantsChangedNotification(sessionId));

        var group = SessionHub.GroupName(sessionId);
        Assert.Equal($"group:{group}|except:conn-origin", hubContext.Sent[0].Audience);
        Assert.Equal("DocumentChanged", hubContext.Sent[0].Method);
        Assert.Equal($"group:{group}", hubContext.Sent[1].Audience);
        Assert.All(hubContext.Sent, s => Assert.Contains(sessionId.ToString("N"), s.Audience, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Relay_aborts_removed_participants_and_ended_sessions()
    {
        using var app = new TestApp();
        var created = await app.CreateSessionAsync();
        var guest = (await app.JoinAsync(created.FormattedCode)).Credentials;
        var registry = new HubConnectionRegistry();
        var (ownerHub, ownerCtx, _) = NewHub(app, registry, "owner-conn");
        var (guestHub, guestCtx, _) = NewHub(app, registry, "guest-conn");
        await ownerHub.Attach(created.PublicId, created.Credentials.ParticipantId, created.Credentials.Token);
        await guestHub.Attach(guest.PublicId, guest.ParticipantId, guest.Token);
        var sessionId = (await app.LoadAsync(created.PublicId)).Id;
        var hubContext = new RecordingHubContext();
        var relay = new HubNotificationRelay(app.Bus, hubContext, registry, NullLogger<HubNotificationRelay>.Instance);

        await relay.RelayAsync(new ParticipantRemovedNotification(sessionId, guest.ParticipantId));
        Assert.True(guestCtx.Aborted);
        Assert.False(ownerCtx.Aborted);
        Assert.Contains(hubContext.Sent, s => s.Audience == "client:guest-conn" && s.Method == "Removed");

        await relay.RelayAsync(new SessionEndedNotification(sessionId, SessionEndReason.Expired));
        Assert.True(ownerCtx.Aborted);
        Assert.Contains(hubContext.Sent, s => s.Method == "SessionEnded" && (string?)s.Payload == "expired");
    }

    [Fact]
    public async Task Disconnect_updates_presence()
    {
        using var app = new TestApp();
        var created = await app.CreateSessionAsync();
        var registry = new HubConnectionRegistry();
        var (hub, _, _) = NewHub(app, registry, "c");
        await hub.Attach(created.PublicId, created.Credentials.ParticipantId, created.Credentials.Token);
        var sessionId = (await app.LoadAsync(created.PublicId)).Id;
        Assert.Contains(created.Credentials.ParticipantId, app.Presence.GetOnlineParticipants(sessionId));

        await hub.OnDisconnectedAsync(null);

        Assert.Empty(app.Presence.GetOnlineParticipants(sessionId));
    }
}
