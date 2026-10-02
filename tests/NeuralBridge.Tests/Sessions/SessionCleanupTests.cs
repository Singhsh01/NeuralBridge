using NeuralBridge.Application.Common;
using NeuralBridge.Application.Realtime;
using NeuralBridge.Domain.Sessions;
using NeuralBridge.Infrastructure.Persistence.InMemory;
using NeuralBridge.Tests.TestSupport;

namespace NeuralBridge.Tests.Sessions;

public class SessionCleanupTests
{
    [Fact]
    public async Task Sweep_expires_overdue_sessions_purges_content_and_notifies()
    {
        using var app = new TestApp();
        var shortLived = await app.CreateSessionAsync(lifetimeMinutes: 15);
        var longLived = await app.CreateSessionAsync(lifetimeMinutes: 60);
        await app.Sync.UpdateAsync(shortLived.Credentials, "will vanish", 0, null);
        await app.Sync.UpdateAsync(longLived.Credentials, "stays", 0, null);

        app.Time.Advance(TimeSpan.FromMinutes(16));
        var report = await app.Cleanup.SweepAsync();

        Assert.Equal(1, report.Expired);
        Assert.Equal(SessionStatus.Expired, (await app.LoadAsync(shortLived.PublicId)).Status);
        Assert.Equal(SessionStatus.Active, (await app.LoadAsync(longLived.PublicId)).Status);
        Assert.Equal(1, app.Documents.Count);
        var ended = Assert.Single(app.NotificationsOf<SessionEndedNotification>());
        Assert.Equal(SessionEndReason.Expired, ended.Reason);
        Assert.Equal(SessionError.Expired, (await app.Sync.UpdateAsync(shortLived.Credentials, "late", 1, null)).Error);
    }

    [Fact]
    public async Task Expired_sessions_reject_access_even_before_the_sweeper_runs()
    {
        using var app = new TestApp();
        var created = await app.CreateSessionAsync(lifetimeMinutes: 15);
        app.Time.Advance(TimeSpan.FromMinutes(15));

        Assert.Equal(SessionError.Expired, (await app.Sync.GetDocumentAsync(created.Credentials)).Error);
        Assert.Equal(SessionError.Expired, (await app.Owner.ExtendAsync(created.Credentials, 15)).Error);
    }

    [Fact]
    public async Task Ended_session_metadata_is_deleted_after_retention()
    {
        using var app = new TestApp(o => o.EndedSessionRetentionMinutes = 60);
        var created = await app.CreateSessionAsync(lifetimeMinutes: 15);
        app.Time.Advance(TimeSpan.FromMinutes(15));
        await app.Cleanup.SweepAsync();

        app.Time.Advance(TimeSpan.FromMinutes(59));
        Assert.Equal(0, (await app.Cleanup.SweepAsync()).Deleted);

        app.Time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(1, (await app.Cleanup.SweepAsync()).Deleted);
        Assert.Equal(0, ((InMemorySessionStore)app.Store).SessionCount);
        Assert.DoesNotContain(((InMemorySessionStore)app.Store).Events, e => e.SessionId != Guid.Empty);

        var join = await app.Sessions.JoinAsync(new(created.FormattedCode, null, null, TestApp.ClientKey));
        Assert.Equal(SessionError.NotFoundOrPinIncorrect, join.Error);
    }

    [Fact]
    public async Task ExpireIfDue_is_idempotent()
    {
        using var app = new TestApp();
        var created = await app.CreateSessionAsync(lifetimeMinutes: 15);
        var id = (await app.LoadAsync(created.PublicId)).Id;

        Assert.False(await app.Cleanup.ExpireIfDueAsync(id));
        app.Time.Advance(TimeSpan.FromMinutes(15));
        Assert.True(await app.Cleanup.ExpireIfDueAsync(id));
        Assert.False(await app.Cleanup.ExpireIfDueAsync(id));
        Assert.Single(app.NotificationsOf<SessionEndedNotification>());
    }

    [Fact]
    public async Task Orphaned_documents_are_removed()
    {
        using var app = new TestApp();
        app.Documents.GetOrCreate(Guid.NewGuid(), app.Time.GetUtcNow());

        var report = await app.Cleanup.SweepAsync();

        Assert.Equal(1, report.OrphanDocumentsRemoved);
        Assert.Equal(0, app.Documents.Count);
    }
}
