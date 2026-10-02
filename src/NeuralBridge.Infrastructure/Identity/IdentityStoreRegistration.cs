using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NeuralBridge.Infrastructure.Persistence;
using NeuralBridge.Infrastructure.Persistence.EntityFramework;

namespace NeuralBridge.Infrastructure.Identity;

public static class IdentityStoreRegistration
{
    /// <summary>EF Core stores for SQLite/PostgreSQL; process-memory stores for the InMemory provider.</summary>
    public static IdentityBuilder AddNeuralBridgeUserStore(this IdentityBuilder builder, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var persistence = configuration.GetSection(PersistenceOptions.SectionName).Get<PersistenceOptions>() ?? new PersistenceOptions();
        if (persistence.Provider == PersistenceProvider.InMemory)
        {
            builder.Services.AddSingleton<InMemoryUserDatabase>();
            return builder.AddUserStore<InMemoryUserStore>();
        }

        return builder.AddNeuralBridgeIdentityStores();
    }
}
