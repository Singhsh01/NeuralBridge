using NeuralBridge.Application.Common;
using NeuralBridge.Application.Documents;
using NeuralBridge.Application.Realtime;
using NeuralBridge.Domain.Documents;
using NeuralBridge.Tests.TestSupport;

namespace NeuralBridge.Tests.Documents;

public class TextSynchronizationTests
{
    [Fact]
    public async Task Update_bumps_version_and_broadcasts_with_origin()
    {
        using var app = new TestApp();
        var created = await app.CreateSessionAsync();
        var guest = (await app.JoinAsync(created.FormattedCode, name: "Work laptop")).Credentials;

        var result = await app.Sync.UpdateAsync(guest, "https://example.org and a prompt", 0, "circuit-guest");

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.Value!.Version);
        var change = Assert.Single(app.NotificationsOf<DocumentChangedNotification>());
        Assert.Equal("circuit-guest", change.OriginId);
        Assert.Equal("Work laptop", change.EditorName);

        var doc = await app.Sync.GetDocumentAsync(created.Credentials);
        Assert.Equal("https://example.org and a prompt", doc.Value!.Content);
        Assert.Equal("Work laptop", doc.Value.LastEditorName);
    }

    [Fact]
    public async Task Edits_flow_in_both_directions()
    {
        using var app = new TestApp();
        var created = await app.CreateSessionAsync();
        var guest = (await app.JoinAsync(created.FormattedCode)).Credentials;

        await app.Sync.UpdateAsync(created.Credentials, "from owner", 0, "a");
        var afterGuest = await app.Sync.UpdateAsync(guest, "from owner + guest", 1, "b");

        Assert.Equal(2, afterGuest.Value!.Version);
        Assert.Equal("from owner + guest", (await app.Sync.GetDocumentAsync(created.Credentials)).Value!.Content);
    }

    [Fact]
    public async Task Identical_text_is_a_no_op()
    {
        using var app = new TestApp();
        var created = await app.CreateSessionAsync();
        await app.Sync.UpdateAsync(created.Credentials, "same", 0, null);

        var again = await app.Sync.UpdateAsync(created.Credentials, "same", 1, null);

        Assert.False(again.Value!.Changed);
        Assert.Equal(1, again.Value.Version);
        Assert.Single(app.NotificationsOf<DocumentChangedNotification>());
    }

    [Fact]
    public async Task Content_over_the_limit_is_rejected()
    {
        using var app = new TestApp(o => o.MaxContentLength = 1000);
        var created = await app.CreateSessionAsync();

        Assert.True((await app.Sync.UpdateAsync(created.Credentials, new string('a', 1000), 0, null)).Succeeded);
        var tooLong = await app.Sync.UpdateAsync(created.Credentials, new string('a', 1001), 1, null);

        Assert.Equal(SessionError.ValidationFailed, tooLong.Error);
        Assert.Equal(1000, (await app.Sync.GetDocumentAsync(created.Credentials)).Value!.Content.Length);
    }

    [Fact]
    public async Task Last_write_wins_and_reports_overwritten_concurrent_edit()
    {
        using var app = new TestApp();
        var created = await app.CreateSessionAsync();
        var guest = (await app.JoinAsync(created.FormattedCode)).Credentials;

        await app.Sync.UpdateAsync(created.Credentials, "owner v1", 0, "a");
        var stale = await app.Sync.UpdateAsync(guest, "guest based on v0", 0, "b");

        Assert.True(stale.Value!.OverwroteConcurrentChange);
        Assert.Equal("guest based on v0", (await app.Sync.GetDocumentAsync(created.Credentials)).Value!.Content);
    }

    [Fact]
    public async Task Clear_empties_the_document_for_editors()
    {
        using var app = new TestApp();
        var created = await app.CreateSessionAsync();
        await app.Sync.UpdateAsync(created.Credentials, "text", 0, null);

        var cleared = await app.Sync.ClearAsync(created.Credentials, "a");

        Assert.True(cleared.Succeeded);
        Assert.Equal(string.Empty, (await app.Sync.GetDocumentAsync(created.Credentials)).Value!.Content);
    }

    [Fact]
    public async Task Updates_are_isolated_per_session()
    {
        using var app = new TestApp();
        var a = await app.CreateSessionAsync();
        var b = await app.CreateSessionAsync();

        await app.Sync.UpdateAsync(a.Credentials, "only in A", 0, null);

        Assert.Equal(string.Empty, (await app.Sync.GetDocumentAsync(b.Credentials)).Value!.Content);
        var change = Assert.Single(app.NotificationsOf<DocumentChangedNotification>());
        Assert.Equal((await app.LoadAsync(a.PublicId)).Id, change.SessionId);
    }

    [Fact]
    public async Task Update_rate_limit_applies_per_participant()
    {
        using var app = new TestApp(configureRateLimits: o => o.DocumentUpdatesPerSecond = 3);
        var created = await app.CreateSessionAsync();

        var results = new List<SessionError>();
        for (var i = 0; i < 5; i++)
        {
            results.Add((await app.Sync.UpdateAsync(created.Credentials, $"v{i}", i, null)).Error);
        }

        Assert.Equal(3, results.Count(e => e == SessionError.None));
        Assert.Contains(SessionError.RateLimited, results);
    }

    [Fact]
    public async Task Typing_notification_requires_edit_rights()
    {
        using var app = new TestApp();
        var created = await app.CreateSessionAsync();
        var guest = (await app.JoinAsync(created.FormattedCode, name: "Tablet")).Credentials;

        Assert.True((await app.Sync.NotifyTypingAsync(guest, "t")).Succeeded);
        Assert.Equal("Tablet", Assert.Single(app.NotificationsOf<TypingNotification>()).DisplayName);

        await app.Owner.SetGuestEditingAsync(created.Credentials, false);
        Assert.Equal(SessionError.ReadOnly, (await app.Sync.NotifyTypingAsync(guest, "t")).Error);
    }

    [Fact]
    public void Merge_strategy_does_not_flag_own_sequential_edits_as_conflicts()
    {
        var strategy = new LastWriteWinsMergeStrategy();
        var me = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var doc = SharedDocument.Empty(Guid.NewGuid(), now).WithContent("a", me, now);

        // My own in-flight edit based on v0 arriving after my v1: not a conflict with someone else.
        var result = strategy.Merge(doc, new DocumentEdit("ab", 0, me), now);

        Assert.False(result.OverwroteConcurrentChange);
        Assert.Equal(2, result.Document.Version);
    }

    [Fact]
    public async Task Concurrent_updates_produce_unique_sequential_versions()
    {
        using var app = new TestApp();
        var created = await app.CreateSessionAsync();

        var tasks = Enumerable.Range(1, 50)
            .Select(i => app.Sync.UpdateAsync(created.Credentials, $"text {i}", 0, null))
            .ToList();
        var results = await Task.WhenAll(tasks);

        var versions = results.Select(r => r.Value!.Version).Order().ToList();
        Assert.Equal(Enumerable.Range(1, 50).Select(i => (long)i), versions);
    }
}
