using Agent.Infrastructure.Tests.Support;
using Agent.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Agent.Infrastructure.Tests.Persistence;

/// <summary>
/// One in-memory SQLite database per test: a single open connection shared by every pooled context, the
/// persistence services registered through <c>AddAgentPersistenceSqlite</c>, and a hand-driven clock.
/// </summary>
public sealed class SqliteDatabase : IDisposable
{
    public SqliteDatabase()
    {
        Connection = new SqliteConnection("Data Source=:memory:");
        Connection.Open();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(Time);
        services.AddAgentPersistenceSqlite(Connection);
        Services = services.BuildServiceProvider();

        using var db = Factory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    public SqliteConnection Connection { get; }

    public ServiceProvider Services { get; }

    public MutableTimeProvider Time { get; } = new();

    public IDbContextFactory<AgentDbContext> Factory => Services.GetRequiredService<IDbContextFactory<AgentDbContext>>();

    public T Get<T>()
        where T : notnull
        => Services.GetRequiredService<T>();

    public void Dispose()
    {
        Services.Dispose();
        Connection.Dispose();
    }
}
