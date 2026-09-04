using Agent.Core.Audit;
using Agent.Core.Authorization;
using Agent.Core.Channels;
using Agent.Infrastructure.Tests.Support;
using Agent.Persistence.Stores;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Agent.Infrastructure.Tests.Persistence;

public sealed class EfAuditSinkTests : IDisposable
{
    private readonly SqliteDatabase _db = new();

    [Fact]
    public async Task WriteAsync_PersistsRowAndLogsLine()
    {
        var logger = new ListLogger<EfAuditSink>();
        var sink = new EfAuditSink(_db.Factory, logger);
        Assert.IsType<EfAuditSink>(_db.Get<IAuditSink>());

        var entry = new AuditEntry(
            new DateTimeOffset(2026, 9, 4, 8, 30, 0, TimeSpan.FromHours(2)),
            Channel.Mattermost,
            "Mattermost:u1",
            "alice",
            "command:deploy",
            "allowed",
            "args=prod",
            new HashSet<Role> { Role.Team, Role.Users });

        await sink.WriteAsync(entry);

        await using var ctx = await _db.Factory.CreateDbContextAsync();
        var row = Assert.Single(await ctx.Audit.ToListAsync());
        Assert.Equal(new DateTime(2026, 9, 4, 6, 30, 0, DateTimeKind.Utc), row.Timestamp);
        Assert.Equal("Mattermost", row.Channel);
        Assert.Equal("Mattermost:u1", row.CallerId);
        Assert.Equal("alice", row.CallerName);
        Assert.Equal("command:deploy", row.Action);
        Assert.Equal("allowed", row.Outcome);
        Assert.Equal("args=prod", row.Detail);
        Assert.Equal("Users,Team", row.Roles);

        var line = Assert.Single(logger.Lines);
        Assert.Equal(LogLevel.Information, line.Level);
        Assert.Contains("audit allowed command:deploy", line.Message);
        Assert.Contains("roles=[Users,Team]", line.Message);
    }

    [Fact]
    public async Task WriteAsync_NullRoles_StoresNull()
    {
        var sink = new EfAuditSink(_db.Factory, new ListLogger<EfAuditSink>());

        await sink.WriteAsync(new AuditEntry(DateTimeOffset.UtcNow, Channel.Cli, "Cli:me", null, "ask", "denied", null, null));

        await using var ctx = await _db.Factory.CreateDbContextAsync();
        var row = Assert.Single(await ctx.Audit.ToListAsync());
        Assert.Null(row.Roles);
        Assert.Null(row.CallerName);
    }

    [Fact]
    public async Task WriteAsync_DatabaseUnavailable_LogsErrorAndDoesNotThrow()
    {
        var logger = new ListLogger<EfAuditSink>();
        var sink = new EfAuditSink(_db.Factory, logger);
        _db.Connection.Dispose();

        await sink.WriteAsync(new AuditEntry(DateTimeOffset.UtcNow, Channel.Cli, "Cli:me", "me", "ask", "allowed", null, null));

        Assert.Contains(logger.Lines, l => l.Level == LogLevel.Error && l.Exception is not null && l.Message.Contains("Failed to persist audit entry"));
    }

    public void Dispose() => _db.Dispose();
}
