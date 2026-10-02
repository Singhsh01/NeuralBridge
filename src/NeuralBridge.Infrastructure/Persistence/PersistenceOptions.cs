namespace NeuralBridge.Infrastructure.Persistence;

public enum PersistenceProvider
{
    Sqlite,
    PostgreSql,

    /// <summary>Process memory only. Useful for demos and tests. Accounts and saved snippets are not available.</summary>
    InMemory,
}

/// <summary>Bound from <c>NeuralBridge:Persistence</c>.</summary>
public sealed class PersistenceOptions
{
    public const string SectionName = "NeuralBridge:Persistence";

    public PersistenceProvider Provider { get; set; } = PersistenceProvider.Sqlite;

    /// <summary>
    /// Name of the connection string under <c>ConnectionStrings</c> to use. Defaults to the
    /// provider name ("Sqlite" or "PostgreSql").
    /// </summary>
    public string? ConnectionStringName { get; set; }

    /// <summary>Apply migrations (or create the schema when no migrations exist) at startup.</summary>
    public bool InitializeDatabaseOnStartup { get; set; } = true;

    public bool IsRelational => Provider != PersistenceProvider.InMemory;
}

/// <summary>Prepares the database at startup.</summary>
public interface IDatabaseInitializer
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
}

public sealed class NoOpDatabaseInitializer : IDatabaseInitializer
{
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
