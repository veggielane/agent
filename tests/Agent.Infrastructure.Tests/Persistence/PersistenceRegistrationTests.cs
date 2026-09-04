using Agent.Core;
using Agent.Core.Audit;
using Agent.Core.Events;
using Agent.Core.Tasks;
using Agent.Persistence;
using Agent.Persistence.Hosting;
using Agent.Persistence.Stores;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Agent.Infrastructure.Tests.Persistence;

public sealed class PersistenceRegistrationTests
{
    [Fact]
    public async Task AddAgentPersistenceSqlite_ReplacesCoreStoresAndCreatesSchemaOnStart()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Llm:BaseUrl"] = "https://llm.internal/v1",
            ["Llm:ApiKey"] = "key",
        }).Build();

        var services = new ServiceCollection();
        services.AddAgentCore(configuration);
        services.AddAgentPersistenceSqlite(connection);
        await using var provider = services.BuildServiceProvider();

        Assert.IsType<EfTaskStore>(provider.GetRequiredService<ITaskStore>());
        Assert.IsType<EfCursorStore>(provider.GetRequiredService<ICursorStore>());
        Assert.IsType<EfProcessedEventStore>(provider.GetRequiredService<IProcessedEventStore>());
        Assert.IsType<EfAuditSink>(provider.GetRequiredService<IAuditSink>());
        Assert.Single(provider.GetServices<ITaskStore>());

        var options = provider.GetRequiredService<IOptionsMonitor<PersistenceOptions>>().CurrentValue;
        Assert.Equal(PersistenceProvider.Sqlite, options.Provider);
        Assert.True(options.MigrateOnStartup);

        var hosted = provider.GetServices<IHostedService>().ToList();
        Assert.Contains(hosted, h => h is RetentionHostedService);
        var migration = Assert.Single(hosted.OfType<MigrationHostedService>());

        await migration.StartAsync(CancellationToken.None);

        var store = provider.GetRequiredService<ITaskStore>();
        var task = await store.AddAsync(new AgentTask { SourceRef = "x", RequesterId = "r", RequesterName = "r", ConversationId = "c", Instruction = "i" });
        Assert.Equal(1, task.Id);
    }

    [Fact]
    public void AddAgentPersistence_BindsOptionsAndSelectsProvider()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Persistence:Provider"] = "Sqlite",
            ["Persistence:ConnectionString"] = "Data Source=:memory:",
            ["Persistence:RetentionDays"] = "30",
            ["Persistence:MigrateOnStartup"] = "false",
        }).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgentPersistence(configuration);
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptionsMonitor<PersistenceOptions>>().CurrentValue;
        Assert.Equal(PersistenceProvider.Sqlite, options.Provider);
        Assert.Equal(30, options.RetentionDays);
        Assert.False(options.MigrateOnStartup);

        using var context = provider.GetRequiredService<IDbContextFactory<AgentDbContext>>().CreateDbContext();
        Assert.True(context.Database.IsSqlite());
    }

    [Fact]
    public void SqlServerMigrations_MatchTheModel()
    {
        using var context = new AgentDbContextDesignTimeFactory().CreateDbContext([]);

        Assert.True(context.Database.IsSqlServer());
        Assert.Contains(context.Database.GetMigrations(), m => m.EndsWith("_InitialCreate", StringComparison.Ordinal));
        Assert.False(context.Database.HasPendingModelChanges(), "The model changed: run `dotnet ef migrations add <Name> --project src/Agent.Persistence --startup-project src/Agent.Persistence --output-dir Migrations`.");
    }
}
