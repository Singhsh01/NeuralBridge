using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NeuralBridge.Application.Abstractions;
using NeuralBridge.Domain.Sessions;
using NeuralBridge.Domain.Snippets;
using NeuralBridge.Infrastructure.Identity;
using NeuralBridge.Infrastructure.Persistence.EntityFramework;
using NeuralBridge.Tests.TestSupport;

namespace NeuralBridge.Tests.Persistence;

/// <summary>Runs the real EF Core model against an in-memory SQLite database (schema from EnsureCreated).</summary>
public sealed class EfPersistenceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TestDbContextFactory _factory;

    public EfPersistenceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<NeuralBridgeDbContext>().UseSqlite(_connection).Options;
        _factory = new TestDbContextFactory(options);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task Full_service_flow_works_on_the_relational_store()
    {
        using var app = new TestApp(store: new EfSessionStore(_factory));
        var created = await app.CreateSessionAsync(pin: "4821");
        var joined = await app.JoinAsync(created.FormattedCode, pin: "4821", name: "Laptop");

        var snapshot = await app.Sessions.GetSnapshotAsync(joined.Credentials);
        Assert.Equal(2, snapshot.Value!.Participants.Count);

        await app.Owner.SetGuestEditingAsync(created.Credentials, false);
        var rotated = await app.Owner.RotateCodeAsync(created.Credentials);
        Assert.True(rotated.Succeeded);
        Assert.True((await app.Owner.CloseAsync(created.Credentials)).Succeeded);

        await using var db = _factory.CreateDbContext();
        var session = await db.Sessions.Include(s => s.Participants).SingleAsync();
        Assert.Equal(SessionStatus.Closed, session.Status);
        Assert.Null(session.PinHash);
        Assert.False(session.GuestEditingEnabled);
        Assert.All(session.Participants, p => Assert.False(p.IsActive));
        Assert.True(await db.SessionEvents.CountAsync() >= 5);
    }

    [Fact]
    public async Task Expiry_queries_compare_timestamps_in_sqlite()
    {
        using var app = new TestApp(store: new EfSessionStore(_factory));
        await app.CreateSessionAsync(lifetimeMinutes: 15);
        await app.CreateSessionAsync(lifetimeMinutes: 60);
        app.Time.Advance(TimeSpan.FromMinutes(20));

        Assert.Equal(1, (await app.Cleanup.SweepAsync()).Expired);

        app.Time.Advance(TimeSpan.FromHours(2));
        var report = await app.Cleanup.SweepAsync();
        Assert.Equal(1, report.Expired);
        Assert.Equal(1, report.Deleted);
    }

    [Fact]
    public async Task Unique_join_code_violation_is_translated()
    {
        var store = new EfSessionStore(_factory);
        var now = DateTimeOffset.UtcNow;
        await using (var uow = store.Begin())
        {
            uow.Add(SharedSession.Create(Guid.NewGuid(), "p1", SessionCode.FromNormalized("AAAA1111BBBB"), now, TimeSpan.FromHours(1), 5, null, null));
            await uow.SaveChangesAsync();
        }

        await using var second = store.Begin();
        second.Add(SharedSession.Create(Guid.NewGuid(), "p2", SessionCode.FromNormalized("AAAA1111BBBB"), now, TimeSpan.FromHours(1), 5, null, null));
        await Assert.ThrowsAsync<DuplicateSessionCodeException>(() => second.SaveChangesAsync());
    }

    [Fact]
    public async Task Snippets_are_scoped_to_their_owner()
    {
        await using (var db = _factory.CreateDbContext())
        {
            db.Users.AddRange(new ApplicationUser { Id = "u1", UserName = "u1" }, new ApplicationUser { Id = "u2", UserName = "u2" });
            await db.SaveChangesAsync();
        }

        var repo = new EfSnippetRepository(_factory);
        var snippet = SavedSnippet.Create("u1", null, "keep me", 1000, DateTimeOffset.UtcNow);
        await repo.AddAsync(snippet);

        Assert.Single(await repo.ListAsync("u1"));
        Assert.Empty(await repo.ListAsync("u2"));
        Assert.False(await repo.DeleteAsync("u2", snippet.Id));
        Assert.True(await repo.DeleteAsync("u1", snippet.Id));
    }

    [Fact]
    public async Task Account_history_retention_and_account_deletion_work_on_the_relational_store()
    {
        await using (var db = _factory.CreateDbContext())
        {
            db.Users.Add(new ApplicationUser { Id = "owner-1", UserName = "owner-1", DisplayName = "Owner", CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        using var app = new TestApp(o => o.AccountHistoryRetentionDays = 30, store: new EfSessionStore(_factory));
        var snippets = new EfSnippetRepository(_factory);
        var accounts = new NeuralBridge.Application.Sessions.AccountDataService(
            app.Store, app.Owner, snippets, app.Locks, app.Time,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<NeuralBridge.Application.Sessions.AccountDataService>.Instance);

        async Task<NeuralBridge.Application.Sessions.CreatedSession> Create(bool retain, string? owner) =>
            (await app.Sessions.CreateAsync(new NeuralBridge.Application.Sessions.CreateSessionCommand("Host", null, 15, owner, TestApp.ClientKey, RetainHistory: retain))).Value!;

        var kept = await Create(retain: true, owner: "owner-1");
        var guest = await Create(retain: false, owner: null);
        await app.Owner.CloseAsync(kept.Credentials);
        await app.Owner.CloseAsync(guest.Credentials);

        app.Time.Advance(TimeSpan.FromDays(1));
        await app.Cleanup.SweepAsync();
        var history = await accounts.ListHistoryAsync("owner-1");
        Assert.Equal(kept.PublicId, Assert.Single(history).PublicId);
        Assert.Null(await app.LoadAsync(guest.PublicId));

        var running = await Create(retain: true, owner: "owner-1");
        await snippets.AddAsync(SavedSnippet.Create("owner-1", null, "keep me", 1000, app.Time.GetUtcNow()));
        await accounts.DeleteAllAsync("owner-1");
        Assert.Null(await app.LoadAsync(kept.PublicId));
        Assert.Null(await app.LoadAsync(running.PublicId));
        Assert.Empty(await snippets.ListAsync("owner-1"));
    }

    private sealed class TestDbContextFactory(DbContextOptions<NeuralBridgeDbContext> options) : IDbContextFactory<NeuralBridgeDbContext>
    {
        public NeuralBridgeDbContext CreateDbContext() => new(options);
    }
}
