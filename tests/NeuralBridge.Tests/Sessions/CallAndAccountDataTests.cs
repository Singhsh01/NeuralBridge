using Microsoft.Extensions.Logging.Abstractions;
using NeuralBridge.Application.Calls;
using NeuralBridge.Application.Common;
using NeuralBridge.Application.Options;
using NeuralBridge.Application.Realtime;
using NeuralBridge.Application.Sessions;
using NeuralBridge.Domain.Sessions;
using NeuralBridge.Infrastructure.Realtime;
using NeuralBridge.Tests.TestSupport;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace NeuralBridge.Tests.Sessions;

public class InMemoryCallRegistryTests
{
    private static CallMemberState Member(Guid id) => new(id, true, false, DateTimeOffset.UnixEpoch);

    [Fact]
    public void Capacity_is_enforced_but_existing_members_can_update()
    {
        var registry = new InMemoryCallRegistry();
        var session = Guid.NewGuid();
        var a = Guid.NewGuid();
        Assert.True(registry.Upsert(session, Member(a), 2));
        Assert.True(registry.Upsert(session, Member(Guid.NewGuid()), 2));
        Assert.False(registry.Upsert(session, Member(Guid.NewGuid()), 2));
        Assert.True(registry.Upsert(session, Member(a) with { VideoEnabled = true }, 2));
        Assert.True(registry.Members(session).Single(m => m.ParticipantId == a).VideoEnabled);
    }

    [Fact]
    public void Sessions_are_isolated_and_cleared_independently()
    {
        var registry = new InMemoryCallRegistry();
        var one = Guid.NewGuid();
        var two = Guid.NewGuid();
        var p = Guid.NewGuid();
        registry.Upsert(one, Member(p), 6);
        Assert.False(registry.Contains(two, p));
        registry.Upsert(two, Member(Guid.NewGuid()), 6);
        registry.ClearSession(one);
        Assert.Empty(registry.Members(one));
        Assert.Single(registry.Members(two));
        Assert.False(registry.Remove(one, p));
    }
}

public class CallServiceTests
{
    private static CallService Calls(TestApp app, Action<CallOptions>? configure = null)
    {
        var options = new CallOptions();
        configure?.Invoke(options);
        return new CallService(app.Store, app.Authorization, new InMemoryCallRegistry(), app.Bus, app.RateLimiter, app.Time, MsOptions.Create(options), NullLogger<CallService>.Instance);
    }

    [Fact]
    public async Task Members_join_see_each_other_and_media_state_is_shared()
    {
        using var app = new TestApp();
        var calls = Calls(app);
        var host = await app.CreateSessionAsync(name: "Laptop");
        var guest = await app.JoinAsync(host.FormattedCode, name: "Phone");

        Assert.True((await calls.JoinAsync(host.Credentials, audio: true, video: true)).Succeeded);
        var roster = (await calls.JoinAsync(guest.Credentials, audio: true, video: false)).Value!;
        Assert.Equal(["Laptop", "Phone"], roster.Select(r => r.DisplayName));
        Assert.True(roster.Single(r => r.DisplayName == "Phone").IsYou);

        Assert.True((await calls.UpdateMediaAsync(host.Credentials, audio: false, video: true)).Succeeded);
        var seen = (await calls.GetRosterAsync(guest.Credentials)).Value!;
        Assert.False(seen.Single(r => r.DisplayName == "Laptop").AudioEnabled);
        Assert.NotEmpty(app.NotificationsOf<CallStateChangedNotification>());
    }

    [Fact]
    public async Task Signals_are_relayed_only_between_members_of_the_same_call()
    {
        using var app = new TestApp();
        var calls = Calls(app);
        var host = await app.CreateSessionAsync();
        var guest = await app.JoinAsync(host.FormattedCode);
        var bystander = await app.JoinAsync(host.FormattedCode, name: "Not in call");
        var other = await app.CreateSessionAsync(name: "Other session");

        await calls.JoinAsync(host.Credentials, true, false);
        await calls.JoinAsync(guest.Credentials, true, false);
        await calls.JoinAsync(other.Credentials, true, false);

        Assert.True((await calls.SignalAsync(host.Credentials, guest.Credentials.ParticipantId, "{\"sdp\":1}")).Succeeded);
        var relayed = app.NotificationsOf<CallSignalNotification>().Single();
        Assert.Equal(host.Credentials.ParticipantId, relayed.FromParticipantId);
        Assert.Equal(guest.Credentials.ParticipantId, relayed.ToParticipantId);

        // Cross-session target, someone not in the call, a sender not in the call, and self: all refused.
        Assert.Equal(SessionError.Forbidden, (await calls.SignalAsync(host.Credentials, other.Credentials.ParticipantId, "x")).Error);
        Assert.Equal(SessionError.Forbidden, (await calls.SignalAsync(host.Credentials, bystander.Credentials.ParticipantId, "x")).Error);
        Assert.Equal(SessionError.Forbidden, (await calls.SignalAsync(bystander.Credentials, host.Credentials.ParticipantId, "x")).Error);
        Assert.Equal(SessionError.Forbidden, (await calls.SignalAsync(host.Credentials, host.Credentials.ParticipantId, "x")).Error);
        Assert.Single(app.NotificationsOf<CallSignalNotification>());
    }

    [Fact]
    public async Task Forged_credentials_and_oversized_signals_are_rejected()
    {
        using var app = new TestApp();
        var calls = Calls(app, o => o.MaxSignalBytes = 1024);
        var host = await app.CreateSessionAsync();
        var guest = await app.JoinAsync(host.FormattedCode);
        await calls.JoinAsync(host.Credentials, true, false);
        await calls.JoinAsync(guest.Credentials, true, false);

        var forged = host.Credentials with { Token = "not-the-token" };
        Assert.False((await calls.JoinAsync(forged, true, true)).Succeeded);
        Assert.False((await calls.SignalAsync(forged, guest.Credentials.ParticipantId, "x")).Succeeded);
        Assert.Equal(SessionError.ValidationFailed, (await calls.SignalAsync(host.Credentials, guest.Credentials.ParticipantId, new string('a', 1025))).Error);
        Assert.Equal(SessionError.ValidationFailed, (await calls.SignalAsync(host.Credentials, guest.Credentials.ParticipantId, string.Empty)).Error);
    }

    [Fact]
    public async Task Full_calls_and_disabled_calls_are_refused_but_text_keeps_working()
    {
        using var app = new TestApp();
        var calls = Calls(app, o => o.MaxCallParticipants = 2);
        var host = await app.CreateSessionAsync();
        var a = await app.JoinAsync(host.FormattedCode);
        var b = await app.JoinAsync(host.FormattedCode);
        await calls.JoinAsync(host.Credentials, true, false);
        await calls.JoinAsync(a.Credentials, true, false);
        Assert.Equal(SessionError.CapacityReached, (await calls.JoinAsync(b.Credentials, true, false)).Error);

        var off = Calls(app, o => o.Enabled = false);
        Assert.Equal(SessionError.Forbidden, (await off.JoinAsync(b.Credentials, true, false)).Error);
    }

    [Fact]
    public async Task Leaving_works_even_after_the_session_closed()
    {
        using var app = new TestApp();
        var calls = Calls(app);
        var host = await app.CreateSessionAsync();
        var guest = await app.JoinAsync(host.FormattedCode);
        await calls.JoinAsync(host.Credentials, true, false);
        await calls.JoinAsync(guest.Credentials, true, false);

        Assert.True((await calls.LeaveAsync(guest.Credentials)).Succeeded);
        Assert.Single((await calls.GetRosterAsync(host.Credentials)).Value!);

        Assert.True((await app.Owner.CloseAsync(host.Credentials)).Succeeded);
        Assert.True((await calls.LeaveAsync(host.Credentials)).Succeeded);
    }
}

public class AccountDataServiceTests
{
    private const string UserId = "user-1";

    private static AccountDataService Accounts(TestApp app) =>
        new(app.Store, app.Owner, app.SnippetRepository, app.Locks, app.Time, NullLogger<AccountDataService>.Instance);

    private static async Task<CreatedSession> CreateAsync(TestApp app, bool retain, string? owner = UserId)
    {
        var result = await app.Sessions.CreateAsync(new CreateSessionCommand("Host", null, 15, owner, TestApp.ClientKey, RetainHistory: retain));
        Assert.True(result.Succeeded);
        return result.Value!;
    }

    [Fact]
    public async Task History_lists_ended_sessions_the_user_chose_to_keep_without_text()
    {
        using var app = new TestApp();
        var accounts = Accounts(app);
        var kept = await CreateAsync(app, retain: true);
        var notKept = await CreateAsync(app, retain: false);
        var running = await CreateAsync(app, retain: true);
        await app.JoinAsync(kept.FormattedCode);
        await app.Owner.CloseAsync(kept.Credentials);
        await app.Owner.CloseAsync(notKept.Credentials);

        var history = await accounts.ListHistoryAsync(UserId);
        var item = Assert.Single(history);
        Assert.Equal(kept.PublicId, item.PublicId);
        Assert.Equal(2, item.Participants);
        Assert.DoesNotContain(history, h => h.PublicId == running.PublicId);
        Assert.Empty(await accounts.ListHistoryAsync("someone-else"));
    }

    [Fact]
    public async Task Guest_sessions_never_retain_history_even_if_asked()
    {
        using var app = new TestApp();
        var guest = await CreateAsync(app, retain: true, owner: null);
        Assert.False((await app.LoadAsync(guest.PublicId)).RetainHistory);
    }

    [Fact]
    public async Task Cleanup_keeps_history_for_the_account_retention_window_only()
    {
        using var app = new TestApp(o => o.AccountHistoryRetentionDays = 30);
        var kept = await CreateAsync(app, retain: true);
        var guest = await CreateAsync(app, retain: false);
        await app.Owner.CloseAsync(kept.Credentials);
        await app.Owner.CloseAsync(guest.Credentials);

        app.Time.Advance(TimeSpan.FromDays(1));
        await app.Cleanup.SweepAsync();
        Assert.NotNull(await app.LoadAsync(kept.PublicId));
        Assert.Null(await app.LoadAsync(guest.PublicId));

        app.Time.Advance(TimeSpan.FromDays(30));
        await app.Cleanup.SweepAsync();
        Assert.Null(await app.LoadAsync(kept.PublicId));
    }

    [Fact]
    public async Task Clearing_history_leaves_running_sessions_alone()
    {
        using var app = new TestApp();
        var accounts = Accounts(app);
        var ended = await CreateAsync(app, retain: true);
        var running = await CreateAsync(app, retain: true);
        await app.Owner.CloseAsync(ended.Credentials);

        Assert.Equal(1, await accounts.ClearHistoryAsync(UserId));
        Assert.Null(await app.LoadAsync(ended.PublicId));
        Assert.True((await app.LoadAsync(running.PublicId)).IsActive(app.Time.GetUtcNow()));
    }

    [Fact]
    public async Task Deleting_an_account_closes_running_sessions_and_erases_everything()
    {
        using var app = new TestApp();
        var accounts = Accounts(app);
        var running = await CreateAsync(app, retain: true);
        var guest = await app.JoinAsync(running.FormattedCode);
        await app.Sync.UpdateAsync(running.Credentials, "secret text", 0, "t");
        Assert.True((await app.Snippets.SaveFromSessionAsync(UserId, running.Credentials, "note")).Succeeded);
        var ended = await CreateAsync(app, retain: true);
        await app.Owner.CloseAsync(ended.Credentials);

        await accounts.DeleteAllAsync(UserId);

        Assert.Null(await app.LoadAsync(running.PublicId));
        Assert.Null(await app.LoadAsync(ended.PublicId));
        Assert.Empty(await app.Snippets.ListAsync(UserId));
        Assert.False((await app.Sync.GetDocumentAsync(guest.Credentials)).Succeeded);
        Assert.Contains(app.NotificationsOf<SessionEndedNotification>(), n => n.Reason == SessionEndReason.ClosedByOwner);
    }
}
