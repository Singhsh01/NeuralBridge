using NeuralBridge.Application.Common;
using NeuralBridge.Application.Realtime;
using NeuralBridge.Application.Sessions;
using NeuralBridge.Domain.Sessions;
using NeuralBridge.Infrastructure.Persistence.InMemory;
using NeuralBridge.Tests.TestSupport;

namespace NeuralBridge.Tests.Sessions;

public class SessionCreationTests
{
    [Fact]
    public async Task Guest_session_is_created_with_defaults_and_owner_credentials()
    {
        using var app = new TestApp();
        var created = await app.CreateSessionAsync();

        Assert.Matches("^[0-9A-Z]{4}-[0-9A-Z]{4}-[0-9A-Z]{4}$", created.FormattedCode);
        Assert.Equal(app.Time.GetUtcNow().AddHours(1), created.ExpiresAt); // default 1 hour
        Assert.NotEqual(created.PublicId, created.Credentials.ParticipantId.ToString());

        var session = await app.LoadAsync(created.PublicId);
        Assert.Null(session.OwnerUserId);
        var owner = Assert.Single(session.Participants);
        Assert.True(owner.IsOwner);
        Assert.NotEqual(created.Credentials.Token, owner.TokenHash); // only the hash is stored
        Assert.Equal(1, app.Documents.Count);
    }

    [Theory]
    [InlineData(15)]
    [InlineData(60)]
    [InlineData(480)]
    [InlineData(1440)]
    public async Task Each_offered_lifetime_is_accepted(int minutes)
    {
        using var app = new TestApp();
        var created = await app.CreateSessionAsync(lifetimeMinutes: minutes);
        Assert.Equal(app.Time.GetUtcNow().AddMinutes(minutes), created.ExpiresAt);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(90)]
    [InlineData(100000)]
    public async Task Unoffered_lifetimes_are_rejected(int minutes)
    {
        using var app = new TestApp();
        var result = await app.Sessions.CreateAsync(new CreateSessionCommand(null, null, minutes, null, TestApp.ClientKey));
        Assert.Equal(SessionError.ValidationFailed, result.Error);
    }

    [Theory]
    [InlineData("123")]
    [InlineData("this-pin-is-way-too-long-to-be-accepted-123")]
    public async Task Invalid_pins_are_rejected(string pin)
    {
        using var app = new TestApp();
        var result = await app.Sessions.CreateAsync(new CreateSessionCommand(null, pin, null, null, TestApp.ClientKey));
        Assert.Equal(SessionError.ValidationFailed, result.Error);
    }

    [Fact]
    public async Task Pin_is_stored_hashed()
    {
        using var app = new TestApp();
        var created = await app.CreateSessionAsync(pin: "4821");
        var session = await app.LoadAsync(created.PublicId);
        Assert.True(session.HasPin);
        Assert.DoesNotContain("4821", session.PinHash!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Duplicate_generated_code_is_skipped()
    {
        var generator = new ScriptedCodeGenerator("AAAA1111BBBB", "AAAA1111BBBB", "CCCC2222DDDD");
        using var app = new TestApp(codeGenerator: generator);

        var first = await app.CreateSessionAsync();
        var second = await app.CreateSessionAsync();

        Assert.Equal("AAAA-1111-BBBB", first.FormattedCode);
        Assert.Equal("CCCC-2222-DDDD", second.FormattedCode);
        Assert.Equal(3, generator.GenerateCalls);
    }

    [Fact]
    public async Task Collision_detected_only_at_save_time_is_retried()
    {
        var store = new RacingStore("AAAA1111BBBB");
        var generator = new ScriptedCodeGenerator("AAAA1111BBBB", "CCCC2222DDDD");
        using var app = new TestApp(codeGenerator: generator, store: store);

        var created = await app.CreateSessionAsync();

        Assert.Equal("CCCC-2222-DDDD", created.FormattedCode);
        Assert.Equal(1, store.Collisions);
    }

    [Fact]
    public async Task Creation_is_rate_limited_per_client()
    {
        using var app = new TestApp(configureRateLimits: o => o.CreateSessionPerMinute = 2);
        await app.CreateSessionAsync();
        await app.CreateSessionAsync();
        var third = await app.Sessions.CreateAsync(new CreateSessionCommand(null, null, null, null, TestApp.ClientKey));
        Assert.Equal(SessionError.RateLimited, third.Error);

        var otherClient = await app.Sessions.CreateAsync(new CreateSessionCommand(null, null, null, null, "203.0.113.9"));
        Assert.True(otherClient.Succeeded);
    }

    [Fact]
    public async Task Account_sessions_are_listed_for_their_owner_only()
    {
        using var app = new TestApp();
        var mine = await app.CreateSessionAsync(ownerUserId: "user-a");
        await app.CreateSessionAsync(ownerUserId: "user-b");
        await app.CreateSessionAsync();

        var list = await app.Sessions.ListOwnedAsync("user-a");
        var summary = Assert.Single(list);
        Assert.Equal(mine.PublicId, summary.PublicId);
        Assert.Equal(mine.FormattedCode, summary.FormattedCode);
    }

    /// <summary>Simulates another writer inserting the same code between the existence check and the save.</summary>
    private sealed class RacingStore(string contestedCode) : NeuralBridge.Application.Abstractions.ISessionStore
    {
        private readonly InMemorySessionStore _inner = new();

        public int Collisions { get; private set; }

        public string ContestedCode { get; } = contestedCode;

        public NeuralBridge.Application.Abstractions.ISessionUnitOfWork Begin() => new Uow(this, _inner.Begin());

        private sealed class Uow(RacingStore owner, NeuralBridge.Application.Abstractions.ISessionUnitOfWork inner) : NeuralBridge.Application.Abstractions.ISessionUnitOfWork
        {
            private SharedSession? _added;

            public Task<SharedSession?> FindByIdAsync(Guid sessionId, CancellationToken cancellationToken = default) => inner.FindByIdAsync(sessionId, cancellationToken);

            public Task<SharedSession?> FindByPublicIdAsync(string publicId, CancellationToken cancellationToken = default) => inner.FindByPublicIdAsync(publicId, cancellationToken);

            public Task<SharedSession?> FindByCodeAsync(string normalizedCode, CancellationToken cancellationToken = default) => inner.FindByCodeAsync(normalizedCode, cancellationToken);

            public Task<bool> CodeInUseAsync(string normalizedCode, CancellationToken cancellationToken = default) => inner.CodeInUseAsync(normalizedCode, cancellationToken);

            public Task<IReadOnlyList<SharedSession>> ListActiveByOwnerAsync(string userId, CancellationToken cancellationToken = default) => inner.ListActiveByOwnerAsync(userId, cancellationToken);

            public Task<IReadOnlyList<SharedSession>> ListOverdueAsync(DateTimeOffset now, int maxCount, CancellationToken cancellationToken = default) => inner.ListOverdueAsync(now, maxCount, cancellationToken);

            public Task<IReadOnlyList<SharedSession>> ListByOwnerAsync(string userId, CancellationToken cancellationToken = default) => inner.ListByOwnerAsync(userId, cancellationToken);

            public Task<IReadOnlyList<SharedSession>> ListDeletableAsync(DateTimeOffset guestCutoff, DateTimeOffset historyCutoff, int maxCount, CancellationToken cancellationToken = default) => inner.ListDeletableAsync(guestCutoff, historyCutoff, maxCount, cancellationToken);

            public void Add(SharedSession session)
            {
                _added = session;
                inner.Add(session);
            }

            public void Remove(SharedSession session) => inner.Remove(session);

            public void AddEvent(SessionEvent sessionEvent) => inner.AddEvent(sessionEvent);

            public Task SaveChangesAsync(CancellationToken cancellationToken = default)
            {
                if (_added?.JoinCode == owner.ContestedCode)
                {
                    owner.Collisions++;
                    throw new NeuralBridge.Application.Abstractions.DuplicateSessionCodeException();
                }

                return inner.SaveChangesAsync(cancellationToken);
            }

            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }
}

public class SessionJoinTests
{
    [Fact]
    public async Task Valid_code_joins_without_an_account_and_notifies_participants()
    {
        using var app = new TestApp();
        var created = await app.CreateSessionAsync();

        var joined = await app.JoinAsync(created.FormattedCode.ToLowerInvariant(), name: "Laptop");

        Assert.Equal(created.PublicId, joined.PublicId);
        var session = await app.LoadAsync(created.PublicId);
        Assert.Equal(2, session.ActiveParticipantCount);
        Assert.Contains(session.Participants, p => p.DisplayName == "Laptop" && !p.IsOwner);
        Assert.Single(app.NotificationsOf<ParticipantsChangedNotification>());
    }

    [Fact]
    public async Task Missing_display_name_gets_a_guest_fallback()
    {
        using var app = new TestApp();
        var created = await app.CreateSessionAsync();
        var joined = await app.JoinAsync(created.FormattedCode, name: null);
        var snapshot = await app.Sessions.GetSnapshotAsync(joined.Credentials);
        Assert.StartsWith("Guest", snapshot.Value!.MyDisplayName, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-code")]
    [InlineData("1234")]
    public async Task Malformed_codes_are_rejected_before_lookup(string code)
    {
        using var app = new TestApp();
        var result = await app.Sessions.JoinAsync(new JoinSessionCommand(code, null, null, TestApp.ClientKey));
        Assert.Equal(SessionError.InvalidCode, result.Error);
    }

    [Fact]
    public async Task Unknown_code_returns_the_generic_answer()
    {
        using var app = new TestApp();
        var result = await app.Sessions.JoinAsync(new JoinSessionCommand("ZZZZ-ZZZZ-ZZZZ", null, null, TestApp.ClientKey));
        Assert.Equal(SessionError.NotFoundOrPinIncorrect, result.Error);
    }

    [Fact]
    public async Task Expired_session_cannot_be_joined()
    {
        using var app = new TestApp();
        var created = await app.CreateSessionAsync(lifetimeMinutes: 15);
        app.Time.Advance(TimeSpan.FromMinutes(15));

        var result = await app.Sessions.JoinAsync(new JoinSessionCommand(created.FormattedCode, null, null, TestApp.ClientKey));
        Assert.Equal(SessionError.Expired, result.Error);
    }

    [Fact]
    public async Task Closed_session_cannot_be_joined()
    {
        using var app = new TestApp();
        var created = await app.CreateSessionAsync();
        Assert.True((await app.Owner.CloseAsync(created.Credentials)).Succeeded);

        var result = await app.Sessions.JoinAsync(new JoinSessionCommand(created.FormattedCode, null, null, TestApp.ClientKey));
        Assert.Equal(SessionError.Closed, result.Error);
    }

    [Fact]
    public async Task Full_session_reports_capacity_reached()
    {
        using var app = new TestApp(o => o.MaxParticipants = 2);
        var created = await app.CreateSessionAsync();
        await app.JoinAsync(created.FormattedCode);

        var result = await app.Sessions.JoinAsync(new JoinSessionCommand(created.FormattedCode, null, null, TestApp.ClientKey));
        Assert.Equal(SessionError.CapacityReached, result.Error);
    }

    [Fact]
    public async Task Leaving_frees_a_slot()
    {
        using var app = new TestApp(o => o.MaxParticipants = 2);
        var created = await app.CreateSessionAsync();
        var guest = await app.JoinAsync(created.FormattedCode);
        Assert.True((await app.Sessions.LeaveAsync(guest.Credentials)).Succeeded);

        await app.JoinAsync(created.FormattedCode);
    }

    [Fact]
    public async Task Joining_is_rate_limited_including_failed_attempts()
    {
        using var app = new TestApp(configureRateLimits: o => o.JoinSessionPerMinute = 3);
        for (var i = 0; i < 3; i++)
        {
            await app.Sessions.JoinAsync(new JoinSessionCommand("ZZZZ-ZZZZ-ZZZZ", null, null, TestApp.ClientKey));
        }

        var limited = await app.Sessions.JoinAsync(new JoinSessionCommand("ZZZZ-ZZZZ-ZZZZ", null, null, TestApp.ClientKey));
        Assert.Equal(SessionError.RateLimited, limited.Error);
    }
}

public class PinProtectedSessionTests
{
    [Fact]
    public async Task Correct_pin_joins()
    {
        using var app = new TestApp();
        var created = await app.CreateSessionAsync(pin: "4821");
        var joined = await app.JoinAsync(created.FormattedCode, pin: " 4821 ");
        Assert.Equal(created.PublicId, joined.PublicId);
    }

    [Fact]
    public async Task Missing_or_wrong_pin_is_indistinguishable_from_unknown_code()
    {
        using var app = new TestApp();
        var created = await app.CreateSessionAsync(pin: "4821");

        var missing = await app.Sessions.JoinAsync(new JoinSessionCommand(created.FormattedCode, null, null, TestApp.ClientKey));
        var wrong = await app.Sessions.JoinAsync(new JoinSessionCommand(created.FormattedCode, "0000", null, TestApp.ClientKey));
        var unknown = await app.Sessions.JoinAsync(new JoinSessionCommand("ZZZZ-ZZZZ-ZZZZ", "0000", null, TestApp.ClientKey));

        Assert.Equal(SessionError.NotFoundOrPinIncorrect, missing.Error);
        Assert.Equal(SessionError.NotFoundOrPinIncorrect, wrong.Error);
        Assert.Equal(unknown.Error, wrong.Error);
        Assert.Equal(unknown.Message, wrong.Message);
    }

    [Fact]
    public async Task Repeated_wrong_pins_lock_the_session_temporarily()
    {
        using var app = new TestApp(o =>
        {
            o.MaxPinAttempts = 3;
            o.PinLockoutMinutes = 5;
        });
        var created = await app.CreateSessionAsync(pin: "4821");
        for (var i = 0; i < 3; i++)
        {
            await app.Sessions.JoinAsync(new JoinSessionCommand(created.FormattedCode, "0000", null, TestApp.ClientKey));
        }

        var locked = await app.Sessions.JoinAsync(new JoinSessionCommand(created.FormattedCode, "4821", null, TestApp.ClientKey));
        Assert.Equal(SessionError.PinLocked, locked.Error);

        app.Time.Advance(TimeSpan.FromMinutes(5));
        await app.JoinAsync(created.FormattedCode, pin: "4821");
    }
}
