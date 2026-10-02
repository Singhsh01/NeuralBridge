using Microsoft.EntityFrameworkCore;
using NeuralBridge.Application.Abstractions;
using NeuralBridge.Domain.Sessions;
using NeuralBridge.Domain.Snippets;

namespace NeuralBridge.Infrastructure.Persistence.EntityFramework;

/// <summary>Creates a fresh, short-lived <see cref="NeuralBridgeDbContext"/> per unit of work (safe for Blazor circuits).</summary>
public sealed class EfSessionStore : ISessionStore
{
    private readonly IDbContextFactory<NeuralBridgeDbContext> _factory;

    public EfSessionStore(IDbContextFactory<NeuralBridgeDbContext> factory) => _factory = factory;

    public ISessionUnitOfWork Begin() => new EfSessionUnitOfWork(_factory.CreateDbContext());
}

internal sealed class EfSessionUnitOfWork : ISessionUnitOfWork
{
    private readonly NeuralBridgeDbContext _db;

    public EfSessionUnitOfWork(NeuralBridgeDbContext db) => _db = db;

    private IQueryable<SharedSession> Sessions => _db.Sessions.Include(s => s.Participants);

    public Task<SharedSession?> FindByIdAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        Sessions.FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);

    public Task<SharedSession?> FindByPublicIdAsync(string publicId, CancellationToken cancellationToken = default) =>
        Sessions.FirstOrDefaultAsync(s => s.PublicId == publicId, cancellationToken);

    public Task<SharedSession?> FindByCodeAsync(string normalizedCode, CancellationToken cancellationToken = default) =>
        Sessions.FirstOrDefaultAsync(s => s.JoinCode == normalizedCode, cancellationToken);

    public Task<bool> CodeInUseAsync(string normalizedCode, CancellationToken cancellationToken = default) =>
        _db.Sessions.AsNoTracking().AnyAsync(s => s.JoinCode == normalizedCode, cancellationToken);

    public async Task<IReadOnlyList<SharedSession>> ListActiveByOwnerAsync(string userId, CancellationToken cancellationToken = default) =>
        await Sessions.Where(s => s.OwnerUserId == userId && s.Status == SessionStatus.Active)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<SharedSession>> ListOverdueAsync(DateTimeOffset now, int maxCount, CancellationToken cancellationToken = default) =>
        await _db.Sessions.Where(s => s.Status == SessionStatus.Active && s.ExpiresAt <= now)
            .Take(maxCount)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<SharedSession>> ListByOwnerAsync(string userId, CancellationToken cancellationToken = default) =>
        await Sessions.Where(s => s.OwnerUserId == userId).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<SharedSession>> ListDeletableAsync(DateTimeOffset guestCutoff, DateTimeOffset historyCutoff, int maxCount, CancellationToken cancellationToken = default) =>
        await Sessions.Where(s => s.Status != SessionStatus.Active &&
                ((s.RetainHistory && s.EndedAt < historyCutoff) || (!s.RetainHistory && s.EndedAt < guestCutoff)))
            .Take(maxCount)
            .ToListAsync(cancellationToken);

    public void Add(SharedSession session) => _db.Sessions.Add(session);

    public void Remove(SharedSession session) => _db.Sessions.Remove(session);

    public void AddEvent(SessionEvent sessionEvent) => _db.SessionEvents.Add(sessionEvent);

    public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsJoinCodeUniqueViolation(ex))
        {
            throw new DuplicateSessionCodeException("The generated session code is already in use.", ex);
        }
    }

    public ValueTask DisposeAsync() => _db.DisposeAsync();

    // SQLite: "UNIQUE constraint failed: Sessions.JoinCode"; PostgreSQL: 23505 on "IX_Sessions_JoinCode".
    private static bool IsJoinCodeUniqueViolation(DbUpdateException ex) =>
        (ex.InnerException?.Message ?? ex.Message).Contains("JoinCode", StringComparison.OrdinalIgnoreCase);
}

public sealed class EfSnippetRepository : ISnippetRepository
{
    private readonly IDbContextFactory<NeuralBridgeDbContext> _factory;

    public EfSnippetRepository(IDbContextFactory<NeuralBridgeDbContext> factory) => _factory = factory;

    public async Task AddAsync(SavedSnippet snippet, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        db.Snippets.Add(snippet);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SavedSnippet>> ListAsync(string userId, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        return await db.Snippets.AsNoTracking().Where(s => s.UserId == userId).ToListAsync(cancellationToken);
    }

    public async Task<int> DeleteAllAsync(string userId, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        return await db.Snippets.Where(s => s.UserId == userId).ExecuteDeleteAsync(cancellationToken);
    }

    public async Task<bool> DeleteAsync(string userId, Guid snippetId, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var deleted = await db.Snippets.Where(s => s.Id == snippetId && s.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        return deleted > 0;
    }
}
