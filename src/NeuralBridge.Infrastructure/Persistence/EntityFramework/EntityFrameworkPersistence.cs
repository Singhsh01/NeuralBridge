using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NeuralBridge.Application.Abstractions;

namespace NeuralBridge.Infrastructure.Persistence.EntityFramework;

/// <summary>Registers the EF Core provider. All EF-specific wiring lives in this folder.</summary>
public static class EntityFrameworkPersistence
{
    public static void Add(IServiceCollection services, IConfiguration configuration, PersistenceOptions options)
    {
        var name = options.ConnectionStringName ?? options.Provider.ToString();
        var connectionString = configuration.GetConnectionString(name);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"Connection string 'ConnectionStrings:{name}' is required for the {options.Provider} provider.");
        }

        if (options.Provider == PersistenceProvider.Sqlite)
        {
            EnsureSqliteDirectory(connectionString);
        }

        services.AddDbContextFactory<NeuralBridgeDbContext>(db =>
        {
            switch (options.Provider)
            {
                case PersistenceProvider.Sqlite:
                    db.UseSqlite(connectionString);
                    break;
                case PersistenceProvider.PostgreSql:
                    db.UseNpgsql(connectionString);
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported relational provider {options.Provider}.");
            }
        });

        services.AddSingleton<ISessionStore, EfSessionStore>();
        services.AddSingleton<ISnippetRepository, EfSnippetRepository>();
        services.AddSingleton<IDatabaseInitializer, EfDatabaseInitializer>();
    }

    /// <summary>SQLite creates the file but not missing folders (e.g. <c>data/</c>).</summary>
    private static void EnsureSqliteDirectory(string connectionString)
    {
        var dataSource = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString).DataSource;
        if (string.IsNullOrWhiteSpace(dataSource) || dataSource == ":memory:")
        {
            return;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(dataSource));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    /// <summary>Wires ASP.NET Core Identity to the EF Core stores.</summary>
    public static IdentityBuilder AddNeuralBridgeIdentityStores(this IdentityBuilder builder) =>
        builder.AddEntityFrameworkStores<NeuralBridgeDbContext>();
}

/// <summary>
/// Applies migrations when the assembly contains any. Otherwise it creates the schema
/// directly, so a fresh clone runs before anyone has generated migrations. Once migrations
/// are added (see README), they take over automatically.
/// </summary>
public sealed class EfDatabaseInitializer : IDatabaseInitializer
{
    private readonly IDbContextFactory<NeuralBridgeDbContext> _factory;
    private readonly ILogger<EfDatabaseInitializer> _logger;

    public EfDatabaseInitializer(IDbContextFactory<NeuralBridgeDbContext> factory, ILogger<EfDatabaseInitializer> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        if (db.Database.GetMigrations().Any())
        {
            await db.Database.MigrateAsync(cancellationToken);
            _logger.LogInformation("Database migrations applied");
        }
        else
        {
            await db.Database.EnsureCreatedAsync(cancellationToken);
            _logger.LogWarning("No EF Core migrations found; schema created with EnsureCreated. Generate migrations before production use.");
        }
    }
}

/// <summary>Lets <c>dotnet ef</c> create the context without starting the web host.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<NeuralBridgeDbContext>
{
    public NeuralBridgeDbContext CreateDbContext(string[] args)
    {
        var provider = Environment.GetEnvironmentVariable("NEURALBRIDGE_EF_PROVIDER") ?? "Sqlite";
        var builder = new DbContextOptionsBuilder<NeuralBridgeDbContext>();
        if (provider.Equals("PostgreSql", StringComparison.OrdinalIgnoreCase))
        {
            builder.UseNpgsql(Environment.GetEnvironmentVariable("NEURALBRIDGE_EF_CONNECTION")
                ?? "Host=localhost;Database=neuralbridge;Username=neuralbridge;Password=design-time-only");
        }
        else
        {
            builder.UseSqlite(Environment.GetEnvironmentVariable("NEURALBRIDGE_EF_CONNECTION") ?? "Data Source=neuralbridge.design.db");
        }

        return new NeuralBridgeDbContext(builder.Options);
    }
}
