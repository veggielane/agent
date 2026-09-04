using System.Data.Common;
using Agent.Core.Audit;
using Agent.Core.Events;
using Agent.Core.Tasks;
using Agent.Persistence.Hosting;
using Agent.Persistence.Stores;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Agent.Persistence;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// EF Core persistence from the <c>Persistence</c> section: pooled context factory, the durable stores
    /// (replacing Core's in-memory ones), schema migration on startup and the retention job.
    /// Call it before <c>AddAgentCoreHostedServices</c> so the schema exists before the workers start.
    /// </summary>
    public static IServiceCollection AddAgentPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<PersistenceOptions>()
            .Bind(configuration.GetSection(PersistenceOptions.SectionName))
            .ValidateDataAnnotations();

        services.AddPooledDbContextFactory<AgentDbContext>((sp, builder) =>
        {
            var options = sp.GetRequiredService<IOptionsMonitor<PersistenceOptions>>().CurrentValue;
            Configure(builder, options);
        });

        return AddStores(services);
    }

    /// <summary>SQLite persistence for development and tests, e.g. <c>Data Source=agent.db</c>.</summary>
    public static IServiceCollection AddAgentPersistenceSqlite(this IServiceCollection services, string connectionString, Action<PersistenceOptions>? configure = null)
    {
        services.AddOptions<PersistenceOptions>().Configure(o =>
        {
            o.Provider = PersistenceProvider.Sqlite;
            o.ConnectionString = connectionString;
            configure?.Invoke(o);
        });

        services.AddPooledDbContextFactory<AgentDbContext>(builder => builder.UseSqlite(connectionString));
        return AddStores(services);
    }

    /// <summary>SQLite persistence over an already open connection, so an in-memory database is shared by every context.</summary>
    public static IServiceCollection AddAgentPersistenceSqlite(this IServiceCollection services, DbConnection connection, Action<PersistenceOptions>? configure = null)
    {
        services.AddOptions<PersistenceOptions>().Configure(o =>
        {
            o.Provider = PersistenceProvider.Sqlite;
            o.ConnectionString = connection.ConnectionString;
            configure?.Invoke(o);
        });

        services.AddPooledDbContextFactory<AgentDbContext>(builder => builder.UseSqlite(connection));
        return AddStores(services);
    }

    private static void Configure(DbContextOptionsBuilder builder, PersistenceOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            throw new InvalidOperationException($"{PersistenceOptions.SectionName}:{nameof(PersistenceOptions.ConnectionString)} is not configured.");
        }

        switch (options.Provider)
        {
            case PersistenceProvider.Sqlite:
                builder.UseSqlite(options.ConnectionString);
                break;
            case PersistenceProvider.SqlServer:
                builder.UseSqlServer(options.ConnectionString, sql => sql.EnableRetryOnFailure());
                break;
            default:
                throw new InvalidOperationException($"Unknown persistence provider '{options.Provider}'.");
        }
    }

    private static IServiceCollection AddStores(IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);

        services.Replace(ServiceDescriptor.Singleton<ITaskStore, EfTaskStore>());
        services.Replace(ServiceDescriptor.Singleton<ICursorStore, EfCursorStore>());
        services.Replace(ServiceDescriptor.Singleton<IProcessedEventStore, EfProcessedEventStore>());
        services.Replace(ServiceDescriptor.Singleton<IAuditSink, EfAuditSink>());

        services.TryAddSingleton<RetentionPurger>();
        services.AddHostedService<MigrationHostedService>();
        services.AddHostedService<RetentionHostedService>();
        return services;
    }
}
