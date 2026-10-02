using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using NeuralBridge.Domain.Sessions;
using NeuralBridge.Domain.Snippets;
using NeuralBridge.Infrastructure.Identity;

namespace NeuralBridge.Infrastructure.Persistence.EntityFramework;

/// <summary>
/// Relational store for session <b>metadata</b>, Identity and explicitly saved snippets.
/// Live document text is never written here.
/// </summary>
public sealed class NeuralBridgeDbContext : IdentityDbContext<ApplicationUser>
{
    public NeuralBridgeDbContext(DbContextOptions<NeuralBridgeDbContext> options)
        : base(options)
    {
    }

    public DbSet<SharedSession> Sessions => Set<SharedSession>();

    public DbSet<SessionParticipant> Participants => Set<SessionParticipant>();

    public DbSet<SessionEvent> SessionEvents => Set<SessionEvent>();

    public DbSet<SavedSnippet> Snippets => Set<SavedSnippet>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ApplyConfigurationsFromAssembly(typeof(NeuralBridgeDbContext).Assembly);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);

        // SQLite has no native DateTimeOffset type and cannot compare/order the default text
        // representation. All timestamps are UTC (offset 0), so the binary encoding keeps
        // chronological ordering and lets expiry queries run in the database.
        if (Database.IsSqlite())
        {
            configurationBuilder.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToBinaryConverter>();
            configurationBuilder.Properties<DateTimeOffset?>().HaveConversion<DateTimeOffsetToBinaryConverter>();
        }
    }
}
