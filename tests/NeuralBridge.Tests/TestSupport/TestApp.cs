using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NeuralBridge.Application.Abstractions;
using NeuralBridge.Application.Documents;
using NeuralBridge.Application.Location;
using NeuralBridge.Application.Options;
using NeuralBridge.Application.Realtime;
using NeuralBridge.Application.Sessions;
using NeuralBridge.Application.Snippets;
using NeuralBridge.Domain.Sessions;
using NeuralBridge.Infrastructure.Persistence.InMemory;
using NeuralBridge.Infrastructure.Realtime;
using NeuralBridge.Infrastructure.Security;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace NeuralBridge.Tests.TestSupport;

/// <summary>
/// Composes the real application services over in-memory infrastructure, with a fake clock
/// and a fast PIN hasher. Each instance is fully isolated.
/// </summary>
public sealed class TestApp : IDisposable
{
    public const string ClientKey = "198.51.100.7";

    public TestApp(
        Action<SessionOptions>? configureSessions = null,
        Action<RateLimitOptions>? configureRateLimits = null,
        ISessionCodeGenerator? codeGenerator = null,
        ISessionStore? store = null)
    {
        SessionOptions = new SessionOptions();
        configureSessions?.Invoke(SessionOptions);
        RateLimitOptions = new RateLimitOptions
        {
            CreateSessionPerMinute = 1000,
            JoinSessionPerMinute = 1000,
            DocumentUpdatesPerSecond = 1000,
            LocationLookupsPerMinute = 1000,
        };
        configureRateLimits?.Invoke(RateLimitOptions);

        var sessionOptions = MsOptions.Create(SessionOptions);
        Store = store ?? new InMemorySessionStore();
        Codes = codeGenerator ?? new SessionCodeGenerator();
        Hasher = new Pbkdf2SecretHasher(iterations: 1000);
        Tokens = new TokenService();
        RateLimiter = new AppRateLimiter(MsOptions.Create(RateLimitOptions));
        Locks = new SessionLockProvider();
        Documents = new InMemoryDocumentStore();
        Presence = new InMemoryPresenceTracker();
        Bus = new InMemorySessionEventBus(NullLogger<InMemorySessionEventBus>.Instance);
        Bus.SubscribeAll(n =>
        {
            lock (Notifications)
            {
                Notifications.Add(n);
            }

            return Task.CompletedTask;
        });

        Authorization = new SessionAuthorizationService(Store, Tokens, Time);
        var allocator = new SessionCodeAllocator(Codes, sessionOptions, NullLogger<SessionCodeAllocator>.Instance);
        Sessions = new SessionService(Store, Codes, allocator, Hasher, Tokens, RateLimiter, Locks, Documents, Presence, Bus, Authorization, Time, sessionOptions, NullLogger<SessionService>.Instance);
        Owner = new SessionOwnerService(Store, Authorization, Locks, allocator, Hasher, Documents, Presence, Bus, Time, sessionOptions, NullLogger<SessionOwnerService>.Instance);
        Sync = new TextSynchronizationService(Store, Authorization, Documents, new LastWriteWinsMergeStrategy(), Bus, RateLimiter, Time, sessionOptions, NullLogger<TextSynchronizationService>.Instance);
        Cleanup = new SessionCleanupService(Store, Locks, Documents, Presence, Bus, Time, sessionOptions, NullLogger<SessionCleanupService>.Instance);
        SnippetRepository = new InMemorySnippetRepository();
        Snippets = new SnippetService(SnippetRepository, Sync, Time, sessionOptions, NullLogger<SnippetService>.Instance);
        Geocoder = new FakeGeocoder();
        MemoryCache = new MemoryCache(new MemoryCacheOptions());
        Location = new LocationDisplayService(Geocoder, RateLimiter, MemoryCache, MsOptions.Create(new LocationOptions()), NullLogger<LocationDisplayService>.Instance);
    }

    public FakeTimeProvider Time { get; } = new();

    public SessionOptions SessionOptions { get; }

    public RateLimitOptions RateLimitOptions { get; }

    public ISessionStore Store { get; }

    public ISessionCodeGenerator Codes { get; }

    public Pbkdf2SecretHasher Hasher { get; }

    public TokenService Tokens { get; }

    public AppRateLimiter RateLimiter { get; }

    public SessionLockProvider Locks { get; }

    public InMemoryDocumentStore Documents { get; }

    public InMemoryPresenceTracker Presence { get; }

    public InMemorySessionEventBus Bus { get; }

    public List<SessionNotification> Notifications { get; } = [];

    public SessionAuthorizationService Authorization { get; }

    public SessionService Sessions { get; }

    public SessionOwnerService Owner { get; }

    public TextSynchronizationService Sync { get; }

    public SessionCleanupService Cleanup { get; }

    public InMemorySnippetRepository SnippetRepository { get; }

    public SnippetService Snippets { get; }

    public FakeGeocoder Geocoder { get; }

    public MemoryCache MemoryCache { get; }

    public LocationDisplayService Location { get; }

    public IReadOnlyList<T> NotificationsOf<T>()
        where T : SessionNotification
    {
        lock (Notifications)
        {
            return Notifications.OfType<T>().ToList();
        }
    }

    public async Task<CreatedSession> CreateSessionAsync(string? pin = null, int? lifetimeMinutes = null, string? ownerUserId = null, string? name = "Host")
    {
        var result = await Sessions.CreateAsync(new CreateSessionCommand(name, pin, lifetimeMinutes, ownerUserId, ClientKey));
        Assert.True(result.Succeeded, $"Create failed: {result.Error} {result.Message}");
        return result.Value!;
    }

    public async Task<JoinedSession> JoinAsync(string code, string? pin = null, string? name = "Guest")
    {
        var result = await Sessions.JoinAsync(new JoinSessionCommand(code, pin, name, ClientKey));
        Assert.True(result.Succeeded, $"Join failed: {result.Error} {result.Message}");
        return result.Value!;
    }

    public async Task<SharedSession> LoadAsync(string publicId)
    {
        await using var uow = Store.Begin();
        return (await uow.FindByPublicIdAsync(publicId))!;
    }

    public void Dispose()
    {
        RateLimiter.Dispose();
        MemoryCache.Dispose();
    }
}

public sealed class FakeGeocoder : IReverseGeocoder
{
    public GeoPlace? Next { get; set; } = new("Ulm", "Baden-Württemberg", "Germany", "DE");

    public int Calls { get; private set; }

    public (double Lat, double Lon)? LastQuery { get; private set; }

    public Task<GeoPlace?> ReverseAsync(double latitude, double longitude, CancellationToken cancellationToken = default)
    {
        Calls++;
        LastQuery = (latitude, longitude);
        return Task.FromResult(Next);
    }
}

/// <summary>Returns a scripted sequence of codes, then random ones (to exercise collision handling).</summary>
public sealed class ScriptedCodeGenerator : ISessionCodeGenerator
{
    private readonly Queue<string> _codes;
    private readonly SessionCodeGenerator _fallback = new();

    public ScriptedCodeGenerator(params string[] codes) => _codes = new Queue<string>(codes);

    public int GenerateCalls { get; private set; }

    public SessionCode Generate()
    {
        GenerateCalls++;
        return _codes.Count > 0 ? SessionCode.FromNormalized(_codes.Dequeue()) : _fallback.Generate();
    }

    public string GeneratePublicId() => _fallback.GeneratePublicId();
}
