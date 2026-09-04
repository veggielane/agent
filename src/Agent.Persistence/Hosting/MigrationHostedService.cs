using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agent.Persistence.Hosting;

/// <summary>
/// Brings the schema up to date before the other hosted services start: SQL Server gets the checked-in
/// migrations, SQLite (dev / tests) gets <c>EnsureCreated</c>. Register persistence before the workers.
/// </summary>
public sealed class MigrationHostedService : IHostedService
{
    private readonly IDbContextFactory<AgentDbContext> _factory;
    private readonly IOptionsMonitor<PersistenceOptions> _options;
    private readonly ILogger<MigrationHostedService> _logger;

    public MigrationHostedService(
        IDbContextFactory<AgentDbContext> factory,
        IOptionsMonitor<PersistenceOptions> options,
        ILogger<MigrationHostedService> logger)
    {
        _factory = factory;
        _options = options;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.CurrentValue.MigrateOnStartup)
        {
            _logger.LogInformation("Persistence: MigrateOnStartup is off; assuming the schema is current");
            return;
        }

        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        if (db.Database.IsSqlite())
        {
            var created = await db.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Persistence: SQLite schema {Outcome}", created ? "created" : "already present");
            return;
        }

        var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false)).ToList();
        await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Persistence: applied {Count} SQL Server migration(s) [{Migrations}]", pending.Count, string.Join(", ", pending));
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
